import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map, shareReplay } from 'rxjs';
import {
  AsOf, CatalogColumn, GridBlock, GridRequest, Health, Me, Portfolio, Preset, PresetState, RequestInfo,
} from './api.types';

/**
 * The typed Desk API client (README §9.4): the only place that knows URLs. Unsafe methods get the antiforgery
 * header from Angular's XSRF interceptor (XSRF-TOKEN cookie → X-XSRF-TOKEN header, ADR-0005).
 */
@Injectable({ providedIn: 'root' })
export class DeskApi {
  private readonly http = inject(HttpClient);

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

  /** One grid block, with what the status bar needs from the response headers. */
  positions(request: GridRequest): Observable<{ block: GridBlock; info: RequestInfo }> {
    const started = performance.now();
    return this.http.post<GridBlock>(DeskApi.positionsUrl, request, { observe: 'response' }).pipe(
      map((res: HttpResponse<GridBlock>) => ({ block: res.body!, info: DeskApi.info(res, started) })),
    );
  }

  /** The streamed CSV export as a file (README §6 P1: from the API, not the browser cache). */
  exportPositions(request: Omit<GridRequest, 'startRow' | 'endRow'>): Observable<Blob> {
    return this.http.post('/api/positions/export', { ...request, startRow: 0, endRow: 0 }, { responseType: 'blob' });
  }

  static readonly positionsUrl = '/api/positions/query';

  /** Request time, cache status, server time and compressed bytes (from the browser's resource timing). */
  static info(res: HttpResponse<unknown>, started: number): RequestInfo {
    const cache = res.headers.get('X-Cache');
    const timing = /total;dur=([\d.]+)/.exec(res.headers.get('Server-Timing') ?? '');
    const entries = typeof performance.getEntriesByType === 'function'
      ? (performance.getEntriesByType('resource') as PerformanceResourceTiming[]).filter((e) => e.name.endsWith(DeskApi.positionsUrl))
      : [];
    const last = entries.at(-1);
    return {
      ms: Math.round(performance.now() - started),
      cache: cache === 'HIT' || cache === 'MISS' ? cache : null,
      serverMs: timing ? Number(timing[1]) : null,
      bytes: last && last.encodedBodySize > 0 ? last.encodedBodySize : null,
    };
  }
}
