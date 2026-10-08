import { HttpClient, HttpResponse } from '@angular/common/http';
import { DestroyRef, Injectable, inject } from '@angular/core';
import { Observable, defer, finalize, map, shareReplay } from 'rxjs';
import {
  AsOf, CatalogColumn, GridBlock, GridRequest, Health, Me, Portfolio, Preset, PresetState, RequestInfo,
} from './api.types';

/** A positions request in flight: when it started, and whether another one ran at the same time. */
interface InFlight { started: number; overlapped: boolean; }

/**
 * The typed Desk API client (README §9.4): the only place that knows URLs. Unsafe methods get the antiforgery
 * header from Angular's XSRF interceptor (XSRF-TOKEN cookie → X-XSRF-TOKEN header, ADR-0005).
 */
@Injectable({ providedIn: 'root' })
export class DeskApi {
  private readonly http = inject(HttpClient);
  /** Resource timings of positions requests that no response has claimed yet (README §10 status bar bytes). */
  private timings: PerformanceResourceTiming[] = [];
  private readonly inFlight = new Set<InFlight>();
  private readonly observer = this.observeTimings();

  health(): Observable<Health> {
    return this.http.get<Health>('/health');
  }

  login(email: string, password: string): Observable<Me> {
    return this.http.post<Me>('/api/auth/login', { email, password });
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', null);
  }

  me(): Observable<Me> {
    return this.http.get<Me>('/api/me');
  }

  asOf(): Observable<AsOf> {
    return this.http.get<AsOf>('/api/meta/as-of');
  }

  /** The catalog only changes with a reseed: fetched once per session (client-side cache-first, README §7.2). */
  private columns$?: Observable<CatalogColumn[]>;

  columns(): Observable<CatalogColumn[]> {
    this.columns$ ??= this.http.get<CatalogColumn[]>('/api/meta/columns').pipe(shareReplay({ bufferSize: 1, refCount: false }));
    return this.columns$;
  }

  portfolios(): Observable<Portfolio[]> {
    return this.http.get<Portfolio[]>('/api/meta/portfolios');
  }

  presets(page: string): Observable<Preset[]> {
    return this.http.get<Preset[]>(`/api/presets/${encodeURIComponent(page)}`);
  }

  savePreset(page: string, name: string, state: PresetState): Observable<void> {
    return this.http.put<void>(`/api/presets/${encodeURIComponent(page)}`, { name, state });
  }

  deletePreset(page: string, name: string): Observable<void> {
    return this.http.delete<void>(`/api/presets/${encodeURIComponent(page)}`, { params: { name } });
  }

  /** One grid block, with what the status bar needs from the response headers and the resource timing. */
  positions(request: GridRequest): Observable<{ block: GridBlock; info: RequestInfo }> {
    return defer(() => {
      const mine: InFlight = { started: performance.now(), overlapped: this.inFlight.size > 0 };
      for (const other of this.inFlight) other.overlapped = true;
      this.inFlight.add(mine);
      return this.http.post<GridBlock>(DeskApi.positionsUrl, request, { observe: 'response' }).pipe(
        map((res: HttpResponse<GridBlock>) => ({ block: res.body!, info: { ...DeskApi.info(res, mine.started), ...this.claimBytes(mine) } })),
        finalize(() => this.inFlight.delete(mine)),
      );
    });
  }

  /** The streamed CSV export as a file (README §6 P1: from the API, not the browser cache). */
  exportPositions(request: Omit<GridRequest, 'startRow' | 'endRow'>): Observable<Blob> {
    return this.http.post('/api/positions/export', { ...request, startRow: 0, endRow: 0 }, { responseType: 'blob' });
  }

  static readonly positionsUrl = '/api/positions/query';

  /** Request time, cache status and server time, from the response headers. */
  static info(res: HttpResponse<unknown>, started: number): RequestInfo {
    const cache = res.headers.get('X-Cache');
    const timing = /total;dur=([\d.]+)/.exec(res.headers.get('Server-Timing') ?? '');
    return {
      ms: Math.round(performance.now() - started),
      cache: cache === 'HIT' || cache === 'MISS' ? cache : null,
      serverMs: timing ? Number(timing[1]) : null,
      bytes: null,
    };
  }

  /**
   * Compressed bytes from the browser's resource timing, read through an observer, so the page-wide timing buffer
   * is neither cleared nor resized. Each entry goes to one response: the earliest unclaimed one that started after
   * the request did. Every block has the same URL, so when blocks overlap the entry may be a sibling's and the
   * bytes are marked approximate.
   */
  private claimBytes(mine: InFlight): Pick<RequestInfo, 'bytes' | 'bytesApprox'> {
    this.keep(this.observer?.takeRecords() ?? []);
    // Entries older than the oldest request still in flight belong to none (a retried 429, an aborted block).
    const oldest = Math.min(...[...this.inFlight].map((r) => r.started));
    const live = this.timings.filter((e) => e.startTime >= oldest).sort((a, b) => a.startTime - b.startTime);
    const i = live.findIndex((e) => e.startTime >= mine.started);
    const entry = i < 0 ? null : live.splice(i, 1)[0];
    this.timings = live;
    if (!entry || entry.encodedBodySize <= 0) return { bytes: null };
    return mine.overlapped ? { bytes: entry.encodedBodySize, bytesApprox: true } : { bytes: entry.encodedBodySize };
  }

  private keep(entries: PerformanceEntryList): void {
    for (const e of entries) if (e.name.endsWith(DeskApi.positionsUrl)) this.timings.push(e as PerformanceResourceTiming);
  }

  private observeTimings(): PerformanceObserver | null {
    if (typeof PerformanceObserver !== 'function') return null;
    const observer = new PerformanceObserver((list) => this.keep(list.getEntries()));
    observer.observe({ entryTypes: ['resource'] });
    inject(DestroyRef).onDestroy(() => observer.disconnect());
    return observer;
  }
}
