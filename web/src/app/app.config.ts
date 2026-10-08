import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors, withXsrfConfiguration } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { sessionInterceptor } from './core/session.interceptor';
import { XSRF_HEADER, xsrfRefreshInterceptor } from './data-access/xsrf-refresh.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    // ADR-0005: the API's antiforgery cookie/header pair (Angular's defaults, spelled out). The XSRF refresh runs
    // inside the session interceptor, so a refresh that finds the session gone still sends the user to /login.
    provideHttpClient(
      withFetch(),
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: XSRF_HEADER }),
      withInterceptors([sessionInterceptor, xsrfRefreshInterceptor]),
    ),
  ],
};
