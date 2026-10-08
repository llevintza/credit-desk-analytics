import { HttpErrorResponse, HttpEventType, HttpInterceptorFn, HttpRequest, HttpXsrfTokenExtractor } from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, filter, from, map, of, switchMap, throwError } from 'rxjs';

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
 * more: if the retry is rejected too, or the refresh fails, that error goes to the caller. Other 400s, and safe
 * methods, pass through untouched. Angular's XSRF interceptor runs before this one and won't replace a header
 * that's already set, so the retry sets the fresh token itself.
 */
export const xsrfRefreshInterceptor: HttpInterceptorFn = (req, next) => {
  if (/^(GET|HEAD|OPTIONS)$/.test(req.method)) return next(req);
  const tokens = inject(HttpXsrfTokenExtractor);
  return next(req).pipe(
    catchError((e: unknown) =>
      antiforgeryRejection(e).pipe(
        switchMap((rejected) => {
          if (!rejected) return throwError(() => e);
          // The refresh's Sent event comes first: retry once its response (and so its Set-Cookie) has arrived.
          return next(new HttpRequest('GET', ANTIFORGERY_URL)).pipe(
            filter((ev) => ev.type === HttpEventType.Response),
            switchMap(() => next(withToken(req, tokens.getToken()))),
          );
        }),
      ),
    ),
  );
};

function withToken(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token === null ? req : req.clone({ headers: req.headers.set(XSRF_HEADER, token) });
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
