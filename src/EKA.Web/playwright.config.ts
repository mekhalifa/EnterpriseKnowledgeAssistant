import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end against the REAL stack (Step 13's exit criterion): Keycloak sign-in, the API, the
 * ingestion worker, SQL Server, Qdrant and a real model. Nothing is mocked, so it proves PKCE,
 * CORS, upload, indexing, streaming and citations work together — and it makes billed AI calls
 * (one document embedded, one question answered: fractions of a cent).
 *
 * Not part of `npm test`. Prerequisites (docs/steps/step-13-angular-client.md §6):
 *   docker compose -f deploy/docker-compose.yml up -d
 *   dotnet run --project src/EKA.Api --launch-profile http
 *   dotnet run --project src/EKA.Ingestion.Worker
 * then: npm run e2e   (starts `ng serve` itself if it is not already running)
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  timeout: 180_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'npm start',
    url: 'http://localhost:4200',
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
