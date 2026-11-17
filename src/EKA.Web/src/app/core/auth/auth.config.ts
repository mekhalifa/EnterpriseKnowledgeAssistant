import { LogLevel, PassedInitialConfig } from 'angular-auth-oidc-client';
import { RuntimeConfig } from '../config/runtime-config';

/**
 * Sign-in (Step 13, ADR-0033): OpenID Connect authorization code flow with PKCE against the
 * Keycloak realm's public client `eka-cli` — the same issuer, audience and scope the API validates
 * since Step 12 (ADR-0031). No secret exists in the browser; PKCE binds the code to this tab.
 *
 * - `secureRoutes` is the API's `/api/` path and nothing else: the interceptor attaches the access
 *   token only to requests going there, never to Keycloak, a CDN, or a link in an answer. A path,
 *   not just the origin, since Step 14: deployed, the page and the API share one origin.
 * - Silent renew uses the refresh token (rotating in Keycloak), not a hidden iframe: third-party
 *   cookie blocking breaks iframe renew in current browsers.
 * - `ignoreNonceAfterRefresh`: Keycloak's refreshed id token carries no nonce, which is correct
 *   per the spec; without this the library would reject every renewal.
 * - Tokens live in sessionStorage (the library default): per tab, gone when it closes. Still
 *   readable by script, so XSS would expose them; the mitigations are no HTML rendering of model
 *   output and the strict Content-Security-Policy served with the page since Step 14 (ADR-0034).
 *   A backend-for-frontend removes the exposure (ADR-0033).
 */
export const createAuthConfig = (runtime: RuntimeConfig): PassedInitialConfig => ({
  config: {
    authority: runtime.auth.authority,
    clientId: runtime.auth.clientId,
    scope: runtime.auth.scope,
    responseType: 'code',
    redirectUrl: `${window.location.origin}/auth/callback`,
    postLogoutRedirectUri: window.location.origin,
    postLoginRoute: '/chat',
    unauthorizedRoute: '/',
    secureRoutes: [`${runtime.apiBaseUrl}/api/`],
    silentRenew: true,
    useRefreshToken: true,
    renewTimeBeforeTokenExpiresInSeconds: 30,
    ignoreNonceAfterRefresh: true,
    logLevel: LogLevel.Warn,
  },
});
