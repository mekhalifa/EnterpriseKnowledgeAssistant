// Load test for the Compose deployment (Step 14). k6, run from a container on the Compose
// network so it reaches the web tier exactly as a browser's requests would:
//
//   docker run --rm -i --network eka_default -e EKA_PASSWORD=local-dev-only `
//     -v ${PWD}/deploy/tests:/scripts grafana/k6:1.4.2 run /scripts/load.js
//
// Two scenarios, both free (no model call is ever allowed to run):
//
//  * reads   - a steady 40 requests/s of document and conversation listings by three users,
//              through nginx to two API instances. Thresholds: p95 under 500 ms, under 1 %
//              errors. This is the path every page view takes.
//  * limits  - one user fires 40 searches at once. The search bucket allows a burst of
//              RateLimiting:Search:TokenLimit (10). With the buckets shared in Redis, 10 get
//              through across BOTH instances; with per-process buckets it would be ~20. The
//              allowed ones reach the provider, so the scenario sends an empty query, which the
//              validator refuses (400) before any embedding is made: no billed call, but the
//              limiter has already counted it, because rate limiting runs before the endpoint.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const base = __ENV.EKA_BASE_URL || 'http://web:8080';
const tokenUrl = __ENV.EKA_TOKEN_URL || 'http://keycloak:8080/realms/eka/protocol/openid-connect/token';
const password = __ENV.EKA_PASSWORD;

const limited = new Counter('eka_search_429');
const admitted = new Counter('eka_search_admitted');

export const options = {
  scenarios: {
    reads: {
      executor: 'constant-arrival-rate',
      rate: 40,
      timeUnit: '1s',
      duration: '60s',
      preAllocatedVUs: 20,
      maxVUs: 60,
      exec: 'reads',
    },
    limits: {
      executor: 'per-vu-iterations',
      vus: 40,
      iterations: 1,
      startTime: '5s',
      exec: 'limits',
    },
  },
  thresholds: {
    'http_req_duration{scenario:reads}': ['p(95)<500'],
    'http_req_failed{scenario:reads}': ['rate<0.01'],
    // Shared buckets: never more admitted than one burst (plus at most one refill token in
    // the second it takes to fire them).
    eka_search_admitted: ['count<=11'],
  },
};

function token(user) {
  const response = http.post(tokenUrl, {
    grant_type: 'password',
    client_id: 'eka-cli',
    username: user,
    password,
    scope: 'openid eka.access',
  });
  check(response, { 'token issued': (r) => r.status === 200 });
  return response.json('access_token');
}

export function setup() {
  if (!password) {
    throw new Error('Set EKA_PASSWORD (the local realm password).');
  }
  // Access tokens live 5 minutes in the local realm; the run is shorter than that.
  return { alice: token('alice'), carol: token('carol'), dana: token('dana'), bruno: token('bruno') };
}

export function reads(tokens) {
  const users = [tokens.alice, tokens.carol, tokens.dana];
  const user = users[Math.floor(Math.random() * users.length)];
  const params = { headers: { Authorization: `Bearer ${user}` } };

  const response = Math.random() < 0.8
    ? http.get(`${base}/api/documents?page=1&pageSize=20`, params)
    // Not /api/usage: it is administrators only (Step 12), so these users get a correct 403.
    // The first run of this script measured exactly that, as 21 % "errors" (Step 14 drill).
    : http.get(`${base}/api/conversations`, params);

  check(response, { 'read ok': (r) => r.status === 200 });
}

export function limits(tokens) {
  const response = http.post(`${base}/api/search`, JSON.stringify({ query: '' }), {
    headers: { Authorization: `Bearer ${tokens.bruno}`, 'Content-Type': 'application/json' },
    responseCallback: http.expectedStatuses(400, 429),
  });

  if (response.status === 429) {
    limited.add(1);
  } else {
    admitted.add(1);
  }

  check(response, { 'refused before any model call': (r) => r.status === 400 || r.status === 429 });
}
