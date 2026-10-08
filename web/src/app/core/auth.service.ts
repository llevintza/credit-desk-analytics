import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, catchError, map, of, tap } from 'rxjs';
import { Me } from '../data-access/api.types';
import { DeskApi } from '../data-access/desk-api';
import { ScopeService } from './scope.service';

/** The signed-in account (README §7.1). `undefined` until checked, `null` when signed out. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(DeskApi);
  private readonly scope = inject(ScopeService);

  readonly me = signal<Me | null | undefined>(undefined);
  readonly isAdmin = computed(() => this.me()?.roles.includes('admin') ?? false);
  /** Reviewer accounts are short-lived: the user menu shows when access ends. */
  readonly expiresAt = computed(() => this.me()?.expiresAt ?? null);

  /** Resolves the session once (GET /api/me); a 401 means signed out, other errors bubble up. */
  check(): Observable<boolean> {
    const known = this.me();
    if (known !== undefined) return of(known !== null);
    return this.api.me().pipe(
      tap((me) => this.me.set(me)),
      map(() => true),
      catchError((e: unknown) => {
        if (e instanceof HttpErrorResponse && e.status === 401) {
          this.me.set(null);
          return of(false);
        }
        throw e;
      }),
    );
  }

  /** A new session starts with a fresh scope: portfolios are entitlement-scoped (README §6), never inherited. */
  login(email: string, password: string): Observable<Me> {
    return this.api.login(email, password).pipe(tap((me) => this.signedIn(me)));
  }

  logout(): Observable<void> {
    return this.api.logout().pipe(tap(() => this.signedOut()));
  }

  /** The session ended server-side (expired account, lapsed cookie): forget it without calling the API. */
  signedOut(): void {
    this.me.set(null);
    this.scope.reset();
  }

  private signedIn(me: Me): void {
    this.scope.reset();
    this.me.set(me);
  }
}

/**
 * Routes behind the login: no session → /login (README §7: the data is not public). A cold-starting server
 * (502/503, network error) also goes to /login, whose "Waking the server…" state waits for it.
 */
export const authGuard: CanActivateFn = () => {
  const router = inject(Router);
  const toLogin = router.createUrlTree(['/login']);
  return inject(AuthService).check().pipe(
    map((ok) => ok || toLogin),
    catchError(() => of(toLogin)),
  );
};

/** Admin-only pages (Usage): viewers go back to positions. */
export const adminGuard: CanActivateFn = () => {
  const router = inject(Router);
  return inject(AuthService).isAdmin() || router.createUrlTree(['/positions']);
};
