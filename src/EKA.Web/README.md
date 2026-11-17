# EKA.Web

The Angular 21 client of the Enterprise Knowledge Assistant (Step 13).

- Why it is built this way: [docs/steps/step-13-angular-client.md](../../docs/steps/step-13-angular-client.md)
  and [ADR-0033](../../docs/adr/0033-angular-client-pkce-sse-generated-contract.md).
- Commands: the "Web client" block in [CLAUDE.md](../../CLAUDE.md).

`src/app/core/api/schema.ts` is generated from `openapi/eka-api.json` (`npm run generate:api`).
Never edit either by hand: the API's integration tests write the document, and `npm run check:api`
fails when the types are stale.
