ENTERPRISE KNOWLEDGE
ASSISTANT
Architecture Roadmap & Step-by-Step Implementation Plan
12-week delivery plan  |  15 hours per week  |  Enterprise-grade MVP
1. Executive summary
Build an enterprise knowledge assistant that ingests organizational documents, indexes their content, answers questions with grounded citations, and enforces document-level access rules. Deliver the backend foundation first, then retrieval-augmented generation (RAG), evaluation and security, and finally the Angular UI and deployment.
Recommended delivery approach: start as a modular monolith with a separate ingestion worker. Keep boundaries clean so components can be extracted later only when scale or team ownership justifies it.
2. Target architecture
High-level request flow:
•	Angular 21 client → ASP.NET Core API (authentication, conversation endpoints, validation)
•	Application layer → use cases, orchestration, authorization filters, prompt assembly
•	AI layer → Microsoft.Extensions.AI abstractions, provider adapters, chat + embedding clients
•	Retrieval layer → query embedding → Qdrant vector search + metadata/ACL filters
•	Persistence → SQL Server for users/conversations/documents/chunks/prompts/provider metadata
•	Ingestion worker → upload/source intake → text extraction → normalization → chunking → embeddings → Qdrant
•	Cross-cutting → Redis cache/rate limits, Hangfire background jobs, Serilog/OpenTelemetry observability
3. Solution structure
src/
  EKA.Api/                 # HTTP endpoints, auth, DI composition root
  EKA.Application/         # Use cases, interfaces, DTOs, orchestration
  EKA.Domain/              # Entities, value objects, domain rules
  EKA.Infrastructure/      # EF Core, SQL Server, repositories, storage
  EKA.AI/                  # Chat/embedding providers, prompts, RAG components
  EKA.Contracts/           # Shared API contracts (keep dependencies minimal)
  EKA.Ingestion.Worker/    # Background ingestion and re-indexing
tests/
  EKA.Domain.Tests/
  EKA.Application.Tests/
  EKA.IntegrationTests/
  EKA.AI.Tests/
deploy/
  docker-compose.yml
  observability/
4. Delivery roadmap (12 weeks)
Week 1 — Stabilize the foundation
Goal: Fix current domain and container blockers; build and test the solution.
•	Fix DocumentChunk constructor validation order; validatae documentId, chunkIndex and content.
•	Remove the duplicate DocumentChunk.Id if the base EntityAudit<Guid> already supplies Id.
•	Fix Docker Compose: use the official SQL Server image, configure EULA/password/volume/health check, and use service DNS from the API.
•	Review the AddEntities migration and verify it applies cleanly to a fresh database.
•	Add domain tests for Document, DocumentChunk, PromptTemplate and AIProvider.
Exit criteria: A clean `dotnet build`, passing tests, and a working API + SQL Server container.
Week 2 — Persistence and application foundation
Goal: Make core entities and use cases reliable.
•	Review entity relationships, required fields, deletion behavior and indexes.
•	Add a unique index on (DocumentId, ChunkIndex).
•	Decide how PromptTemplate is associated with AIProvider and enforce the relationship consistently.
•	Add repository/unit-of-work abstractions only where they simplify use cases.
•	Add API health checks and consistent error handling.
Exit criteria: Fresh database migration succeeds; CRUD/use-case integration tests pass.
Week 3 — AI provider integration
Goal: Connect chat and embedding providers through abstractions.
•	Register Microsoft.Extensions.AI chat and embedding clients through EKA.AI.
•	Implement provider configuration for OpenAI and Azure OpenAI.
•	Load secrets from User Secrets/environment variables/Key Vault; never persist plaintext API keys.
•	Create a small provider-independent chat service and a connectivity test endpoint restricted to development.
•	Add timeout, cancellation, retry and structured logging policies.
Exit criteria: A test request can call either configured provider without application-layer provider-specific code.
Week 4 — Prompt engineering and chat
Goal: Deliver a basic conversational API.
•	Create prompt templates with versioning and active/inactive state.
•	Define system instructions, user context and conversation history boundaries.
•	Implement conversation and message use cases plus API endpoints.
•	Persist user/assistant messages and provider/model/token metadata where available.
•	Add tests for prompt composition, empty input, long history and provider failures.
Exit criteria: A client can create a conversation, send a question and retrieve persisted messages.
Week 5 — Document ingestion
Goal: Build a repeatable document intake pipeline.
•	Define supported file types and upload limits.
•	Store original files in a storage abstraction; persist document metadata in SQL Server.
•	Extract text, normalize whitespace and retain page/section/source metadata.
•	Implement ingestion status transitions and idempotency.
•	Move long-running work to the ingestion worker using Hangfire or a queue.
Exit criteria: Uploading a supported file creates a tracked document and completes text extraction asynchronously.
Week 6 — Chunking and embeddings
Goal: Transform extracted text into searchable vectors.
•	Implement configurable chunk size and overlap.
•	Preserve document ID, chunk index, page/section, tenant/authority and ACL metadata.
•	Generate embeddings in batches with bounded concurrency and retry handling.
•	Persist chunk metadata in SQL Server and vectors in Qdrant.
•	Support re-indexing and safe deletion of old vectors.
Exit criteria: An ingested document produces ordered chunks and corresponding vectors; repeat ingestion is safe.
Week 7 — Vector retrieval
Goal: Implement semantic search with access-aware filtering.
•	Create a Qdrant collection with the selected embedding dimension and distance metric.
•	Embed incoming questions and retrieve top-K candidate chunks.
•	Apply metadata filters for document status, authority, tenant and user permissions before returning content.
•	Add configurable top-K, score threshold and optional keyword/hybrid retrieval.
•	Build retrieval tests using a small known document corpus.
Exit criteria: Queries retrieve relevant chunks and never return documents outside the caller's access scope.
Week 8 — RAG answer generation
Goal: Generate grounded answers with traceable sources.
•	Compose retrieved context into a bounded prompt.
•	Instruct the model to say when evidence is insufficient rather than fabricate.
•	Return citations with document name and page/section/chunk references.
•	Store retrieval and answer metadata for diagnostics.
•	Add tests for answer grounding, missing evidence, conflicting sources and prompt injection in documents.
Exit criteria: The chat endpoint returns an answer plus source citations; unsupported questions are handled safely.
Week 9 — RAG quality and evaluation
Goal: Measure retrieval and answer quality before adding complexity.
•	Create a small representative evaluation dataset with expected source documents and answer criteria.
•	Measure retrieval precision/recall or hit rate at K.
•	Evaluate groundedness, relevance, citation correctness and refusal behavior.
•	Compare chunking, top-K and prompt configurations using repeatable runs.
•	Add regression tests for the most important question set.
Exit criteria: A repeatable evaluation report establishes a baseline and catches quality regressions.
Week 10 — Security and enterprise controls
Goal: Harden data boundaries and operational behavior.
•	Integrate the intended identity provider and validate token issuer, audience and scopes.
•	Enforce document-level authorization in retrieval, not only in the UI.
•	Add upload validation, content limits, rate limiting and provider cost controls.
•	Protect secrets and sensitive logs; define retention and deletion behavior.
•	Threat-model prompt injection, data exfiltration, cross-authority access and malicious files.
Exit criteria: Security tests demonstrate that users cannot retrieve unauthorized content or secrets.
Week 11 — Angular client and user experience
Goal: Deliver the first usable frontend.
•	Create Angular 21 app structure and typed API client.
•	Implement sign-in integration, conversation list, chat screen and streaming/non-streaming response handling.
•	Render citations with source details and document links.
•	Add upload progress, ingestion status and clear error states.
•	Add accessibility, responsive layout and basic end-to-end tests.
Exit criteria: A user can upload a document, wait for indexing, ask questions and inspect citations.
Week 12 — Deployment and release readiness
Goal: Package, observe and document the MVP.
•	Complete Docker Compose for API, worker, SQL Server, Qdrant and Redis.
•	Add readiness/liveness checks, structured logs, traces and basic metrics.
•	Create environment-specific configuration and deployment pipeline.
•	Document setup, migrations, secrets, backup/restore and troubleshooting.
•	Run load, failure-recovery and security smoke tests; tag the MVP release.
Exit criteria: A reproducible deployment and release checklist are complete; the MVP is ready for controlled users.
5. Implementation sequence — do these in order
1.	Stabilize the current code: fix DocumentChunk validation and duplicate Id; add tests before extending features.
2.	Repair Docker Compose and run SQL Server locally in a container; stop using LocalDB from inside Linux containers.
3.	Apply the EF Core migration to a clean database and verify all expected tables, foreign keys and indexes.
4.	Complete dependency injection and provider configuration in EKA.AI; verify one chat and one embedding call.
5.	Implement conversation persistence and the chat API before building the frontend.
6.	Implement document upload and text extraction; keep ingestion asynchronous.
7.	Add chunking, embeddings and Qdrant storage; persist enough metadata to trace each vector to its source.
8.	Implement access-filtered retrieval, then RAG answer generation with citations.
9.	Build an evaluation dataset and baseline before tuning advanced RAG strategies.
10.	Harden authorization, secrets, rate limits and prompt-injection defenses.
11.	Build the Angular experience only after the backend API contracts are stable.
12.	Deploy the complete stack and verify monitoring, recovery and operational documentation.
6. Definition of Done
•	Code builds with no errors and automated tests pass.
•	Database migrations apply to a fresh environment and can be rolled forward safely.
•	No secrets are committed or stored as plaintext in application tables.
•	Every retrieved chunk is filtered by the caller's effective access rights.
•	Answers expose citations and do not claim evidence that retrieval did not provide.
•	Ingestion is asynchronous, observable, retryable and idempotent.
•	Logs and traces contain correlation IDs but do not leak sensitive document content.
•	Docker-based setup is reproducible from documented instructions.
•	Evaluation and security regression tests run as part of CI.
7. Current repository priorities
Based on the latest repository review available on 10 November 2026:
Priority	Area	Required action
P0	DocumentChunk constructor	Validation currently runs before Content is assigned; new chunks can fail immediately.
P0	Docker Compose database	Infrastructure Dockerfile is missing; use the official SQL Server container image.
P1	Duplicate Id property	Remove the derived Id if the base entity already defines it.
P1	AI service registration	AddAiServices() is currently empty; register chat and embedding providers.
P1	PromptTemplate/provider relation	Make required AIProvider association explicit in constructor or domain method.
P1	Migration verification	Migration was added but must be applied and tested against a clean database.
P2	Chunk ordering constraint	Add a unique (DocumentId, ChunkIndex) index and tests.
8. Weekly working rhythm (15 hours)
Time	Activity
2 hours	Study official documentation and clarify design decisions.
9 hours	Implement the week's scoped tasks.
2 hours	Write unit/integration tests and fix defects.
1 hour	Update README, architecture notes and diagrams.
1 hour	Review progress, risks and next week's backlog.
9. Recommended technical references
•	Microsoft.Extensions.AI: https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai
•	Build AI apps with .NET: https://learn.microsoft.com/en-us/dotnet/ai/
•	EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/
•	SQL Server Linux containers: https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker
•	Qdrant documentation: https://qdrant.tech/documentation/
•	OWASP Top 10 for LLM Applications: https://owasp.org/www-project-top-10-for-large-language-model-applications/
•	Angular documentation: https://angular.dev/
•	ASP.NET Core documentation: https://learn.microsoft.com/en-us/aspnet/core/
10. Scope control — what not to build yet
•	Do not split into multiple microservices during the MVP unless a concrete scaling or ownership need appears.
•	Do not add agents or autonomous workflows before retrieval, grounding and evaluation are reliable.
•	Do not introduce multiple vector databases or complex hybrid search before measuring baseline retrieval.
•	Do not add advanced reranking, graph RAG or multi-agent orchestration until evaluation demonstrates a need.
•	Do not treat frontend visibility as authorization; enforce access rules in backend retrieval.

Project repository: https://github.com/mekhalifa/EnterpriseKnowledgeAssistant
