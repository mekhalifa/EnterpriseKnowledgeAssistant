import { LogLevel, PassedInitialConfig } from 'angular-auth-oidc-client';
import { environment } from '../../../environments/environment';

/**
 * Sign-in (Step 13, ADR-0033): OpenID Connect authorization code flow with PKCE against the
 * Keycloak realm's public client `eka-cli` — the same issuer, audience and scope the API validates
 * since Step 12 (ADR-0031). No secret exists in the browser; PKCE binds the code to this tab.
 *
 * - `secureRoutes` is the API's origin and nothing else: the interceptor attaches the access token
 *   only to requests going there, never to Keycloak, a CDN, or a link in an answer.
 * - Silent renew uses the refresh token (rotating in Keycloak), not a hidden iframe: third-party
 *   cookie blocking breaks iframe renew in current browsers.
 * - `ignoreNonceAfterRefresh`: Keycloak's refreshed id token carries no nonce, which is correct
 *   per the spec; without this the library would reject every renewal.
 * - Tokens live in sessionStorage (the library default): per tab, gone when it closes. Still
 *   readable by script, so XSS would expose them; the mitigations are no HTML rendering of model
 *   output and a strict CSP in Step 14. A backend-for-frontend removes the exposure (ADR-0033).
 */
export const authConfig: PassedInitialConfig = {
  config: {
    authority: environment.auth.authority,
    clientId: environment.auth.clientId,
    scope: environment.auth.scope,
    responseType: 'code',
    redirectUrl: `${window.location.origin}/auth/callback`,
    postLogoutRedirectUri: window.location.origin,
    postLoginRoute: '/chat',
    unauthorizedRoute: '/',
    secureRoutes: [environment.apiBaseUrl],
    silentRenew: true,
    useRefreshToken: true,
    renewTimeBeforeTokenExpiresInSeconds: 30,
    ignoreNonceAfterRefresh: true,
    logLevel: LogLevel.Warn,
  },
};
