import { Injectable, computed, signal } from '@angular/core';

/** The body of a login or refresh (ASP.NET Core Identity's AccessTokenResponse). */
export interface Tokens { tokenType: string; accessToken: string; expiresIn: number; refreshToken: string; }

/** sessionStorage key of the refresh token. The API's Swagger UI script reads the same key (SwaggerSetup.RefreshTokenKey). */
export const REFRESH_TOKEN_KEY = 'desk.refresh';

/**
 * Where the bearer tokens live (ADR-0024). The access token only in memory; the refresh token in this tab's
 * sessionStorage, so a reload or a deep link keeps the session and closing the tab ends it. Both are readable by
 * any script on the page (the XSS trade-off the ADR accepts); sessionStorage is never sent anywhere by the browser.
 * Storage can be blocked (privacy modes): then the session lasts until the access token expires.
 */
@Injectable({ providedIn: 'root' })
export class TokenStore {
  private readonly access = signal<string | null>(null);
  private readonly refresh = signal<string | null>(read());

  readonly accessToken = this.access.asReadonly();
  readonly refreshToken = this.refresh.asReadonly();
  /** A token to send or one to refresh with: worth asking the API who is signed in. */
  readonly hasSession = computed(() => this.access() !== null || this.refresh() !== null);

  set(tokens: Tokens): void {
    this.access.set(tokens.accessToken);
    this.refresh.set(tokens.refreshToken);
    write(tokens.refreshToken);
  }

  clear(): void {
    this.access.set(null);
    this.refresh.set(null);
    write(null);
  }
}

function read(): string | null {
  try {
    return sessionStorage.getItem(REFRESH_TOKEN_KEY);
  } catch {
    return null;
  }
}

function write(value: string | null): void {
  try {
    if (value === null) sessionStorage.removeItem(REFRESH_TOKEN_KEY);
    else sessionStorage.setItem(REFRESH_TOKEN_KEY, value);
  } catch {
    // Blocked storage: the in-memory signal still holds it for this page.
  }
}
