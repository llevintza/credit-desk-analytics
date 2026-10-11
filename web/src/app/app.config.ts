import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withFetch, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { apiBaseInterceptor } from './core/api-base';
import { authInterceptor } from './core/auth.interceptor';
import { sessionInterceptor } from './core/session.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding()),
    // ADR-0024: bearer tokens, so no XSRF cookie to echo. The token refresh runs inside the session interceptor, so a
    // refused refresh still sends the user to /login; the API origin is applied last, to the refresh as well.
    provideHttpClient(
      withFetch(),
      withNoXsrfProtection(),
      withInterceptors([sessionInterceptor, authInterceptor, apiBaseInterceptor]),
    ),
  ],
};
