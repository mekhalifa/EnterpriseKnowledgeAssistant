/**
 * Settings read when the page loads, not when the bundle is built (Step 14, ADR-0034).
 *
 * Twelve-factor "build, release, run": one build of the client runs in every environment, and
 * what differs between them (the identity provider, where the API lives) is supplied at deploy
 * time. In development `public/config.json` is served by `ng serve`; in the container, nginx
 * writes it from environment variables when it starts (deploy/web/40-runtime-config.sh).
 *
 * Nothing here is a secret: a browser client has none. The OIDC client is public and proves
 * itself with PKCE (ADR-0033).
 */
export interface RuntimeConfig {
  /**
   * Origin of the API, or `''` for the page's own origin (deployed behind one reverse proxy).
   * Requests go to `${apiBaseUrl}/api/...`, and only those carry the access token.
   */
  apiBaseUrl: string;
  auth: {
    /** Must be exactly the issuer (`iss`) the API validates. */
    authority: string;
    clientId: string;
    scope: string;
  };
}

/** Throws with a readable message when the file is missing a field, instead of failing later. */
export function parseRuntimeConfig(value: unknown): RuntimeConfig {
  const config = value as Partial<RuntimeConfig> | null;
  const auth = config?.auth;

  if (
    typeof config?.apiBaseUrl !== 'string' ||
    typeof auth?.authority !== 'string' ||
    auth.authority === '' ||
    typeof auth.clientId !== 'string' ||
    auth.clientId === '' ||
    typeof auth.scope !== 'string'
  ) {
    throw new Error('config.json must define apiBaseUrl, auth.authority, auth.clientId and auth.scope.');
  }

  if (config.apiBaseUrl.endsWith('/')) {
    throw new Error('config.json apiBaseUrl must not end with a slash.');
  }

  return { apiBaseUrl: config.apiBaseUrl, auth: { authority: auth.authority, clientId: auth.clientId, scope: auth.scope } };
}

/** Fetched before Angular starts, so every provider sees final values. Never cached. */
export async function loadRuntimeConfig(url = '/config.json'): Promise<RuntimeConfig> {
  const response = await fetch(url, { cache: 'no-store' });
  if (!response.ok) {
    throw new Error(`Could not load ${url}: HTTP ${response.status}.`);
  }

  return parseRuntimeConfig(await response.json());
}
