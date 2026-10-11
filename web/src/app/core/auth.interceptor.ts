import {
  HttpErrorResponse,
  HttpEventType,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, filter, finalize, map, shareReplay, switchMap, take, throwError } from 'rxjs';
import { TokenStore, Tokens } from './token.store';

export const REFRESH_URL = '/api/auth/refresh';
/** Calls that carry their own credentials (a password or a refresh token), never a bearer token. */
const OWN_CREDENTIALS = /^\/api\/auth\/(login|refresh)$/;

/**
 * Adds `Authorization: Bearer` to every API call (ADR-0024). After a reload only the refresh token is left, so the
 * first call refreshes before it goes. A 401 refreshes once and retries once; if the refresh is refused (its own
 * 401) the tokens are gone and the 401 reaches sessionInterceptor, which signs out. A retry that fails again is
 * not retried. Concurrent 401s share one refresh, and a 401 for a token a sibling already replaced just retries
 * with the new one. Other errors, the login and refresh calls, and URLs outside the API pass through untouched.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith('/api/') || OWN_CREDENTIALS.test(req.url)) return next(req);
  const tokens = inject(TokenStore);
  const refresher = inject(TokenRefresh);
  const send = (token: string | null) => next(token === null ? req : req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));

  const sent = tokens.accessToken();
  if (sent === null && tokens.refreshToken() !== null) return refresher.run(next).pipe(switchMap(send));
  return send(sent).pipe(
    catchError((e: unknown) => {
      if (!(e instanceof HttpErrorResponse) || e.status !== 401) return throwError(() => e);
      const latest = tokens.accessToken();
      if (latest !== null && latest !== sent) return send(latest);
      if (tokens.refreshToken() === null) return throwError(() => e);
      return refresher.run(next).pipe(switchMap(send));
    }),
  );
};

/** The one POST /api/auth/refresh in flight, shared by every request that needs it while it runs. */
@Injectable({ providedIn: 'root' })
export class TokenRefresh {
  private readonly tokens = inject(TokenStore);
  private inflight$: Observable<string> | null = null;

  /**
   * Emits the new access token. A refused refresh (401) forgets both tokens; any other failure (a cold start, a
   * 429 or 503) keeps them, so a blip doesn't sign anyone out, and the error goes to the caller.
   */
  run(next: HttpHandlerFn): Observable<string> {
    this.inflight$ ??= next(new HttpRequest('POST', REFRESH_URL, { refreshToken: this.tokens.refreshToken() })).pipe(
      filter((ev): ev is HttpResponse<Tokens> => ev.type === HttpEventType.Response),
      take(1),
      map((res) => {
        this.tokens.set(res.body!);
        return res.body!.accessToken;
      }),
      catchError((e: unknown) => {
        if (e instanceof HttpErrorResponse && e.status === 401) this.tokens.clear();
        return throwError(() => e);
      }),
      finalize(() => (this.inflight$ = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inflight$;
  }
}
