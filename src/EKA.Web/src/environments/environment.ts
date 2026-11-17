/**
 * Local development settings (Step 13). Nothing here is a secret: a browser client has none.
 * The OIDC client is public and proves itself with PKCE, not a client secret (ADR-0033).
 *
 * The authority must be exactly the issuer the API validates (`iss`), which is why it is
 * 127.0.0.1 and not localhost: Keycloak stamps the host it was reached through into the token.
 */
export const environment = {
  apiBaseUrl: 'http://localhost:5118',
  auth: {
    authority: 'http://127.0.0.1:8080/realms/eka',
    clientId: 'eka-cli',
    // eka.access is what the API demands; openid makes it an OIDC sign-in (an id token).
    scope: 'openid eka.access',
  },
};
