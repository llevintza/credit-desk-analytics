import { HttpInterceptorFn } from '@angular/common/http';
import { InjectionToken, inject } from '@angular/core';

/**
 * The API's origin, set at build time (ADR-0024): `ng build --define 'DESK_API_BASE_URL="https://…"'` for the
 * GitHub Pages build. angular.json defines it as "" (same origin: the API-served app, compose, e2e, the dev proxy).
 */
declare const DESK_API_BASE_URL: string;

/** The API's origin with no trailing slash, or "" for the page's own origin. */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => apiBase(DESK_API_BASE_URL),
});

export function apiBase(raw: string): string {
  return raw.trim().replace(/\/+$/, '');
}

/** DeskApi's URLs are all root-relative: `/api/…` and the `/health` probe. */
export function isApiUrl(url: string): boolean {
  return url.startsWith('/api/') || url === '/health';
}

/**
 * Sends DeskApi's root-relative URLs to the configured API origin. Last in the chain, so the interceptors before it
 * still see `/api/…` and the token refresh they start goes to the API too. Anything else is left alone.
 */
export const apiBaseInterceptor: HttpInterceptorFn = (req, next) => {
  const base = inject(API_BASE_URL);
  return base && isApiUrl(req.url) ? next(req.clone({ url: base + req.url })) : next(req);
};
