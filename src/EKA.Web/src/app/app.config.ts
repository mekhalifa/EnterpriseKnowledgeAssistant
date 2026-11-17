import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { authInterceptor, provideAuth, withAppInitializerAuthCheck } from 'angular-auth-oidc-client';
import { routes } from './app.routes';
import { API_BASE_URL } from './core/api/api';
import { createAuthConfig } from './core/auth/auth.config';
import { RuntimeConfig } from './core/config/runtime-config';

/** Built from the runtime configuration that main.ts loads before Angular starts (Step 14). */
export const createAppConfig = (runtime: RuntimeConfig): ApplicationConfig => ({
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor()])),
    // Completes a sign-in redirect (?code=…) before the first route renders, so a guarded page
    // never sees a half-signed-in state.
    provideAuth(createAuthConfig(runtime), withAppInitializerAuthCheck()),
    { provide: API_BASE_URL, useValue: runtime.apiBaseUrl },
  ],
});
