import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * A 401 from a data call means the session ended under us (an expired reviewer account, a lapsed cookie):
 * forget it and go to the login page instead of leaving the page retrying forever. The session check and the
 * login call handle their own 401s.
 */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return next(req).pipe(
    catchError((e: unknown) => {
      if (e instanceof HttpErrorResponse && e.status === 401 && !/\/api\/(me|auth\/login)$/.test(req.url) && auth.me()) {
        auth.signedOut();
        void router.navigate(['/login']);
      }
      return throwError(() => e);
    }),
  );
};
