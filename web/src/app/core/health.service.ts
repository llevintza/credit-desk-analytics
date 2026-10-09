import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, retry, timer } from 'rxjs';
import { DeskApi } from '../data-access/desk-api';

export type ApiState =
  | { kind: 'waking'; attempt: number }
  | { kind: 'ready'; version: string; maintenance: boolean }
  | { kind: 'unreachable' };

/**
 * Reads the static /health probe (never touches the database). The free Render instance can take 30–60 s to
 * cold-start (README §13.2), so it retries every 2 s for up to ~90 s, reporting each attempt for the progress UI.
 */
@Injectable({ providedIn: 'root' })
export class HealthService {
  private readonly api = inject(DeskApi);
  static readonly retryEveryMs = 2000;
  static readonly maxAttempts = 45;

  state(): Observable<ApiState> {
    return new Observable<ApiState>((subscriber) => {
      let attempt = 0;
      subscriber.next({ kind: 'waking', attempt });
      const poll = this.api.health().pipe(
        retry({
          count: HealthService.maxAttempts,
          delay: () => {
            subscriber.next({ kind: 'waking', attempt: ++attempt });
            return timer(HealthService.retryEveryMs);
          },
        }),
        map((h): ApiState => ({ kind: 'ready', version: h.version, maintenance: h.maintenance === true })),
        catchError(() => of<ApiState>({ kind: 'unreachable' })),
        // eslint-disable-next-line desk/no-unmanaged-subscribe -- inner subscribe of a `new Observable` factory: the teardown below unsubscribes when the consumer does (ADR-0010).
      ).subscribe(subscriber);
      return () => poll.unsubscribe();
    });
  }
}
