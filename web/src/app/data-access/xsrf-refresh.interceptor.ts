import {
  HttpErrorResponse,
  HttpEventType,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
  HttpXsrfTokenExtractor,
} from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, filter, finalize, from, map, of, shareReplay, switchMap, take, throwError } from 'rxjs';

/** ADR-0005: the header the API reads the XSRF token from (Angular's XSRF interceptor fills it from the cookie). */
export const XSRF_HEADER = 'X-XSRF-TOKEN';
/** The ProblemDetails type of the API's antiforgery 400 (`AntiforgeryFilter.ProblemType`): the stable contract (#282). */
export const ANTIFORGERY_PROBLEM_TYPE = 'urn:desk:problem:antiforgery';
/** Its title (`AntiforgeryFilter.ProblemTitle`), still matched as a fallback for one release (#282). */
export const ANTIFORGERY_PROBLEM_TITLE = 'Missing or invalid antiforgery token';
export const ANTIFORGERY_URL = '/api/auth/antiforgery';

/**
 * A stale or missing XSRF token (#233: a deploy that renames the antiforgery cookie, #268 N6: a sign-out that
 * fails on it) gets one fresh token from GET /api/auth/antiforgery and one retry of the unsafe request. Never
 * more: if the retry is rejected too, or the refresh fails, that error goes to the caller, and if the refresh leaves
 * no token to send, the original 400 does (#310 N2: a retry without one would only be rejected again). Concurrent
 * rejections share one refresh (#310 N3), so no retry races a sibling refresh's Set-Cookie. Other 400s, and safe
 * methods, pass through untouched, and so does any URL outside the API's relative `/api/` path (#310 N1: like
 * Angular's own XSRF interceptor, a cross-origin server never gets the token). Angular's XSRF interceptor runs
 * before this one and won't replace a header that's already set, so the retry sets the fresh token itself.
 */
export const xsrfRefreshInterceptor: HttpInterceptorFn = (req, next) => {
  // DeskApi's URLs are all root-relative `/api/…`: serving the app under a path prefix would need this check widened.
  if (/^(GET|HEAD|OPTIONS)$/.test(req.method) || !req.url.startsWith('/api/')) return next(req);
  const tokens = inject(HttpXsrfTokenExtractor);
  const refresh = inject(XsrfRefresh);
  return next(req).pipe(
    catchError((e: unknown) =>
      antiforgeryRejection(e).pipe(
        switchMap((rejected) => {
          if (!rejected) return throwError(() => e);
          return refresh.run(next).pipe(
            switchMap(() => {
              const token = tokens.getToken();
              if (token === null) return throwError(() => e);
              return next(req.clone({ headers: req.headers.set(XSRF_HEADER, token) }));
            }),
          );
        }),
      ),
    ),
  );
};

/** The one GET /api/auth/antiforgery in flight, shared by every rejection that arrives while it runs. */
@Injectable({ providedIn: 'root' })
export class XsrfRefresh {
  private inflight$: Observable<unknown> | null = null;

  /** Emits once the refresh's response (and so its Set-Cookie) has arrived; its Sent event comes first. */
  run(next: HttpHandlerFn): Observable<unknown> {
    this.inflight$ ??= next(new HttpRequest('GET', ANTIFORGERY_URL)).pipe(
      filter((ev) => ev.type === HttpEventType.Response),
      take(1),
      finalize(() => (this.inflight$ = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.inflight$;
  }
}

/** Whether the error is the API's antiforgery 400. A blob request (the CSV export) gets its problem as a Blob. */
function antiforgeryRejection(e: unknown): Observable<boolean> {
  if (!(e instanceof HttpErrorResponse) || e.status !== 400) return of(false);
  if (!(e.error instanceof Blob)) return of(isAntiforgeryProblem(e.error));
  return from(e.error.text()).pipe(
    map((text) => isAntiforgeryProblem(JSON.parse(text))),
    catchError(() => of(false)),
  );
}

function isAntiforgeryProblem(body: unknown): boolean {
  if (typeof body !== 'object' || body === null) return false;
  const { type, title } = body as { type?: unknown; title?: unknown };
  return type === ANTIFORGERY_PROBLEM_TYPE || title === ANTIFORGERY_PROBLEM_TITLE;
}
