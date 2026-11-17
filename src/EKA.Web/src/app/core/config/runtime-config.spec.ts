import { createAuthConfig } from '../auth/auth.config';
import { parseRuntimeConfig } from './runtime-config';

const valid = {
  apiBaseUrl: '',
  auth: { authority: 'http://127.0.0.1:8080/realms/eka', clientId: 'eka-cli', scope: 'openid eka.access' },
};

describe('parseRuntimeConfig', () => {
  it('accepts the same-origin deployment shape', () => {
    expect(parseRuntimeConfig(valid)).toEqual(valid);
  });

  it('refuses a file that would start the app half-configured', () => {
    expect(() => parseRuntimeConfig({ ...valid, auth: { ...valid.auth, authority: '' } })).toThrow();
    expect(() => parseRuntimeConfig({ auth: valid.auth })).toThrow();
    expect(() => parseRuntimeConfig(null)).toThrow();
  });

  it('refuses a trailing slash, which would produce //api/ paths the token never matches', () => {
    expect(() => parseRuntimeConfig({ ...valid, apiBaseUrl: 'http://localhost:5118/' })).toThrow();
  });
});

describe('createAuthConfig', () => {
  it('sends the access token to the API path only, also when the API shares the page origin', () => {
    const sameOrigin = createAuthConfig(valid).config as { secureRoutes: string[] };
    const separate = createAuthConfig({ ...valid, apiBaseUrl: 'http://localhost:5118' }).config as { secureRoutes: string[] };

    // Not '' (every request, Keycloak's included) and not the bare origin.
    expect(sameOrigin.secureRoutes).toEqual(['/api/']);
    expect(separate.secureRoutes).toEqual(['http://localhost:5118/api/']);
  });
});
