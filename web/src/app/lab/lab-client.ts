import { HttpClient, HttpResponse } from '@angular/common/http';
import { DestroyRef, Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';

/** The query parameter that makes each lab request's URL, and so its resource timing entry, unique. */
export const tagParam = 'lab';

/** How long to wait for the browser to report a finished response's timing entry before giving up (n/a). */
export const timingWaitMs = 1000;

/**
 * The browser's resource timing entries for lab requests (README §6 P5), read through an observer so the page-wide
 * buffer is neither cleared nor resized (it fills at 250 entries, and the status bar reads it too). Entries are
 * keyed by their `lab` tag; each is handed out once.
 */
@Injectable()
export class ResourceTimings {
  private readonly seen = new Map<string, PerformanceResourceTiming>();
  private readonly waiting = new Map<string, (e: PerformanceResourceTiming) => void>();
  private readonly observer = this.observe();

  /** The entry for `tag`, once the browser reports it; null after {@link timingWaitMs} (or with no Resource Timing). */
  entry(tag: string): Promise<PerformanceResourceTiming | null> {
    this.add(this.observer?.takeRecords() ?? []);
    const found = this.take(tag);
    if (found) return Promise.resolve(found);
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.waiting.delete(tag);
        resolve(null);
      }, timingWaitMs);
      this.waiting.set(tag, (e) => {
        clearTimeout(timer);
        resolve(e);
      });
    });
  }

  /** Internal (tests): records entries as the observer would. */
  add(entries: PerformanceEntryList): void {
    for (const e of entries as PerformanceResourceTiming[]) {
      const tag = tagOf(e.name);
      if (tag === null) continue;
      const waiter = this.waiting.get(tag);
      if (waiter) {
        this.waiting.delete(tag);
        waiter(e);
      } else {
        this.seen.set(tag, e);
      }
    }
  }

  private take(tag: string): PerformanceResourceTiming | null {
    const e = this.seen.get(tag) ?? null;
    this.seen.delete(tag);
    return e;
  }

  private observe(): PerformanceObserver | null {
    if (typeof PerformanceObserver !== 'function') return null;
    const observer = new PerformanceObserver((list) => this.add(list.getEntries()));
    observer.observe({ type: 'resource' });
    inject(DestroyRef).onDestroy(() => observer.disconnect());
    return observer;
  }
}

/** The `lab` tag of a resource URL, or null for any other request. */
export function tagOf(url: string): string | null {
  const q = url.indexOf('?');
  return q < 0 ? null : new URLSearchParams(url.slice(q + 1)).get(tagParam);
}

/** One measured exchange: the response, with its body as bytes, and the browser's timing entry for it. */
export interface Exchange { res: HttpResponse<ArrayBuffer>; entry: PerformanceResourceTiming | null; }

/**
 * Sends the lab's requests through the app's HttpClient, so the bearer token, the session handling and the API
 * origin apply exactly as on the pages. The body comes back as bytes, so the page times the decode itself, and the
 * browser cache is skipped (`no-store`): a run never reads a stored copy or sends If-None-Match.
 */
@Injectable()
export class LabClient {
  private readonly http = inject(HttpClient);
  private readonly timings = inject(ResourceTimings);
  private readonly destroyRef = inject(DestroyRef);
  private seq = 0;
  private readonly session = Date.now().toString(36);

  async send(method: 'GET' | 'POST', url: string, headers: Record<string, string>, body?: unknown): Promise<Exchange> {
    const tag = `${this.session}-${++this.seq}`;
    const tagged = `${url}${url.includes('?') ? '&' : '?'}${tagParam}=${tag}`;
    const res = await firstValueFrom(
      this.http.request(method, tagged, { body, headers, observe: 'response', responseType: 'arraybuffer', cache: 'no-store' })
        .pipe(takeUntilDestroyed(this.destroyRef)),
    );
    return { res, entry: await this.timings.entry(tag) };
  }
}
