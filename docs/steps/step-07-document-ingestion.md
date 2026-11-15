# Step 7: Document ingestion

**Status:** ✅ Complete
**Date:** 2026-11-15
**Branch:** `step-07-document-ingestion` (branched from `step-06-chat-api`)
**Result:** 429 tests, zero warnings. **The exit criterion was met twice:** in the
integration suite (the real API and the real worker composition in-process, connected only
by Hangfire), and on the real stack (§6): Kestrel plus a separate worker process.

Before this step a "document" was a metadata row that a client asserted into existence.
Now a client uploads bytes. The server decides what they are, stores them atomically,
records the document, and hands it to a separate process that extracts the text with page
and section metadata. Nothing is lost when the hand-off fails.

---

## 1. What was built

| Piece | Where | Purpose |
|---|---|---|
| `POST /api/documents` (multipart) | `EKA.Api` | **Replaces** Step 4's JSON metadata registration. Size limit declared to Kestrel |
| `UploadDocumentHandler` | `EKA.Application` | Detect → store while hashing → dedupe → insert → enqueue |
| `FileTypeDetector` | `EKA.Application` | Type from the first 512 bytes; the extension must agree |
| `HashingReadStream` | `EKA.Application` | SHA-256 and byte limit in the same pass that stores the file |
| `IFileStorage` → `LocalFileStorage` | ports / `EKA.Infrastructure` | Atomic writes, containment check ([ADR-0021](../adr/0021-file-storage-port-local-disk.md)) |
| `IIngestionQueue` → `HangfireIngestionQueue` | ports / `EKA.Infrastructure` | Best-effort hand-off ([ADR-0020](../adr/0020-hangfire-best-effort-handoff.md)) |
| `ExtractDocumentTextHandler` | `EKA.Application` | Pending → Extracting → Chunking; permanent vs transient failure |
| `TextExtractionPipeline` + 3 extractors | `EKA.Application` / `EKA.Infrastructure` | Chain of Responsibility; PdfPig, Open XML, text/Markdown ([ADR-0022](../adr/0022-pure-dotnet-text-extraction.md)) |
| `ExtractedTextStore` | `EKA.Application` | `<key>.extracted.json`, versioned ([ADR-0023](../adr/0023-extracted-text-artifact.md)) |
| `RecoverStalledDocumentsHandler` | `EKA.Application` | Recurring sweep: re-enqueue documents stuck in Pending |
| `Document.AwaitsExtraction`, `StartOrResumeExtraction` | `EKA.Domain` | The aggregate answers "is work owed?" for redelivered jobs |
| `DuplicateKeyException` | `EKA.Application` + `EkaDbContext` | SQL 2601/2627 translated, with the index name |
| `UX_Documents_TenantId_ContentHash` | migration, by hand | Closes Step 4's registration race |
| `WorkerComposition.AddIngestionWorker` | `EKA.Ingestion.Worker` | The worker's composition, bootable by tests |
| `AddRequestHandlers` / `AddIngestionHandlers` | `EKA.Application` | Each host registers what it runs (§5.4) |

---

## 2. The dual write, and how the design survives it

An upload makes **three writes**: the file, the row, and the job. No transaction spans all
three. The design does not pretend one does. It orders them so that every crash point
leaves a state the system recovers from:

| Crash or failure after… | State left behind | What recovers it |
|---|---|---|
| the file is stored | orphan file, no row | nothing yet: wasted disk, never a wrong answer |
| the row is inserted | document Pending, no job | the sweep, after `StalledAfter` |
| the job is enqueued | normal | the worker |
| the worker saved Extracting | document Extracting, job retried | `StartOrResumeExtraction` |
| the artifact is written | artifact exists, still Extracting | the retry rewrites it whole |

**At-least-once plus idempotent replaces exactly-once.** The worker may receive a document
twice (from the upload and the sweep, or from a retry). It asks the aggregate whether
extraction is still owed, and does nothing if not. That is the whole trick. Exactly-once
delivery across a database and a queue would need a distributed transaction, and nobody
should want one.

**Why the enqueue may fail without failing the upload.** The document is safely recorded.
Returning a 500 would tell the client the upload failed when it succeeded, and the retry
would then hit the content-hash dedupe. `HangfireIngestionQueue` even resolves Hangfire's
client *inside* its try block. Building SQL job storage touches the database, and as a
constructor dependency it would fail the request before the adapter could apply its
contract.

---

## 3. Never trust the declared type

The client controls the filename and the `Content-Type` header. So:

- **The bytes decide.** `%PDF-` is a PDF. A ZIP local-file header is (maybe) a Word file.
  Valid UTF-8 with no NUL bytes is text. Anything else is refused before it is stored.
- **The extension must agree.** A PDF named `.docx`, text named `.pdf` and a ZIP named
  `.zip` are all refused. The extension only picks between formats the bytes cannot tell
  apart (Markdown vs plain text).
- **The stored content type is ours**, derived from detection. A client sending
  `application/x-totally-legit` gets `text/markdown` on the record. A test pins this.
- **The hash is ours.** Step 4 accepted a client-supplied SHA-256, which meant a client
  could claim another document's identity without holding its bytes.
- **The filename is reduced to its last segment.** Clients send `C:\fakepath\x.pdf` or
  `../../x.pdf`. Backslashes are folded first, because on Linux `Path.GetFileName` does not
  treat them as separators.

What detection cannot prove, the extractor proves: a file that starts with `%PDF-` and is
garbage after it fails extraction as `UnreadableDocumentException`, and the document ends
`Failed` with the parser's reason.

---

## 4. Two layers of size limit

1. **Kestrel, while the body streams in.** The endpoint carries
   `RequestSizeLimitAttribute(MaxUploadBytes + 64 KB)`. Kestrel stops reading and answers
   **413** before the body is buffered.
2. **The use case, while it copies.** `HashingReadStream` throws once `MaxUploadBytes` is
   exceeded, and the handler answers **400 `document.file_too_large`**. That protects any
   future caller that is not the HTTP endpoint.

The in-memory `TestServer` does **not** implement request-body limits, so the integration
suite proves layer 2 and that the endpoint *declares* layer 1. Layer 1 itself was verified
against real Kestrel (§6).

---

## 5. What went wrong

### 5.1 A vulnerable transitive dependency, caught by the build

The first Infrastructure build failed with **NU1903**: `Newtonsoft.Json 11.0.1` has a known
high-severity vulnerability (GHSA-5crp-9r3c-p9vr). Hangfire.Core declares
`Newtonsoft.Json >= 11.0.1`, and NuGet resolves **the lowest version that satisfies a
range**, so a vulnerable floor arrives by default. NuGet audit raised it, and
`TreatWarningsAsErrors` made it a failure instead of a yellow line nobody reads.

**Fix:** `CentralPackageTransitivePinningEnabled` plus a `PackageVersion` for Newtonsoft.Json
13.0.4. CPM then overrides the version of that package wherever it arrives indirectly.
**Lesson:** "lowest applicable version" means every library's minimum is your version until
you say otherwise.

### 5.2 The shell ate my backslashes

Three edits passed C# through a heredoc into a Python script. The shell layer collapsed
`\\` to `\`, so `"C:\\fakepath"` became `"C:` + form feed + `akepath"`, and `'\\'` became
`'\'`, which does not compile. One file ended up with literal control characters that text
search could not even match. **Rule adopted:** text containing backslashes goes through the
editor tools, never a shell heredoc.

### 5.3 `[FromForm]` record binding versus the validation filter

Every upload returned 400 "A request body is required". The `ValidationFilter` finds its
request among the endpoint's arguments and treats a missing one as "no body", which is right
for JSON. A `[FromForm]` record bound to **null**. The two form fields are now plain
optional parameters, and the same validator runs explicitly in that one endpoint. The rules
stayed in `UploadDocumentMetadataValidator`.

### 5.4 The worker could not start, and the test host was more lenient than production

The real worker *process* crashed at startup: "Unable to resolve service for type
`IChatCompletionService` while attempting to activate `AskQuestionHandler`". In Development
the .NET host validates the **entire** container at build time (`ValidateOnBuild`), and
`AddApplication()` registered every handler, so the worker carried the chat handler without
its chat client.

**The integration test had passed**, because its in-process worker used
`DisableDefaults = true`, which also switches that validation off. The test booted a more
forgiving host than production.

**Fixes, both structural:** (1) each host registers what it runs: `AddRequestHandlers` for
the API, `AddIngestionHandlers` for the worker, and `IngestionJobs` only in the worker.
(2) The test host now sets `ValidateOnBuild` and `ValidateScopes` explicitly. **Proved:**
with the bug deliberately reintroduced, the test fails with the production error. Reverted
in place, and the worker assembly was confirmed recompiled (the Step 6 §5.3 trap avoided).

### 5.5 Smaller ones

- **Every persistence test using `TestFixtures.NewDocument` shared one content hash**, which
  the new unique index rightly rejected. The fixture now generates a fresh hash per document.
- **Five persistence tests expected `DbUpdateException`** for unique violations, and now get
  `DuplicateKeyException`. They assert the new type *and which index fired*, which is a
  stronger test. The foreign-key test (SQL 547) still expects `DbUpdateException`: only
  unique violations are translated.
- **The old index comment was wrong.** It argued the content-hash index must not be unique
  "because a tenant re-uploads after a Reset". Reset reuses the same row.

---

## 6. The exit criterion on the real stack

The API on Kestrel and the worker as a **separate process**, sharing a throwaway database
and a scratch storage folder:

| Action | Result |
|---|---|
| Upload `benefits.md` | **201**, Pending |
| Upload the same bytes again | **200**, same document (dedupe) |
| Upload `broken.pdf` (`%PDF-` then garbage) | **201**, accepted by detection |
| Upload a 27 MB text file | **413**, from Kestrel |
| A few seconds later | `benefits.md` → **Chunking**, 3 sections: `Benefits`, `Benefits / Annual leave`, `Benefits / Sick leave` |
| | `broken.pdf` → **Failed**: "The PDF could not be read: Could not find any xref tables…" |

Two documents in the tenant, not three. The worker logged one extraction and one
unreadable, and no retries. The demo database and files were deleted afterwards.

---

## 7. Decisions inside the code worth knowing about

- **The unique index lives outside the EF model.** EF cannot index across an owner property
  and an owned-type property. The migration creates it, and `SchemaMigrationTests` is what
  makes depending on it safe.
- **`DuplicateKeyException` is the one persistence exception the application handles.** A
  check-then-insert always loses some race. The loser reads the winner and answers 200.
- **Hangfire tables are created on first use** (with a lock), so the API never touches job
  storage at startup, and the health probe for an unreachable database is unaffected.
- **Job arguments are `Guid`s.** Stored jobs outlive builds, and domain types would tie them
  to a serialization shape forever.
- **Antiforgery is disabled on upload** because authentication is explicit per request, not
  cookies. Revisit if Step 13 uses cookies.
- **The worker's audit identity is `system:ingestion-worker`**, and it has no ambient tenant.
- **Storage defaults to `%LOCALAPPDATA%/EKA/files`**, because "relative to the project" means
  something different in each process.

---

## 8. Alternatives, and when each would win

| Choice | Chosen | Alternative wins when… |
|---|---|---|
| Hand-off | Hangfire + best-effort + sweep ([ADR-0020](../adr/0020-hangfire-best-effort-handoff.md)) | Outbox + broker: a lost hand-off must recover in seconds, or other services consume uploads |
| Storage | Local disk behind a port ([ADR-0021](../adr/0021-file-storage-port-local-disk.md)) | Blob/S3: the API and the worker run on different machines (Step 14) |
| Extraction | PdfPig + Open XML ([ADR-0022](../adr/0022-pure-dotnet-text-extraction.md)) | Document Intelligence: scans, tables or multi-column layouts matter |
| Text store | JSON artifact ([ADR-0023](../adr/0023-extracted-text-artifact.md)) | `DocumentPages` table: text must be queried or edited before chunking |
| Upload contract | Replace JSON registration with multipart | Keep both: a bulk importer registers files that already sit in storage |
| Type check | Magic bytes + extension | Full parse at upload: the API must reject corrupt files synchronously (costs API CPU, ADR-0004) |

---

## 9. What to take away from Step 7

1. **Name every write and its crash state.** Two systems cannot commit together, so order
   the writes so that each crash point leaves something recoverable, and build the recovery
   (the sweep), not just the happy path.
2. **At-least-once plus idempotent beats exactly-once.** Ask the aggregate "is work still
   owed?" instead of trying to make delivery perfect.
3. **Untrusted input is the bytes, the name and the claimed type.** Measure the first, check
   the second against it, and ignore the third.
4. **A test host more lenient than production is a lie.** Defaults that a test disables,
   like build-time validation, must be restored on purpose.
5. **"Lowest applicable version" is a supply-chain policy.** Without transitive pinning,
   every library's minimum dependency version becomes yours.

---

## Next

**Step 8: chunking and embeddings.** Read the artifact, chunk with configurable size and
overlap while preserving page and section metadata, embed in batches with bounded
concurrency through `IEmbeddingService`, and write vectors to Qdrant. Re-ingestion must be
safe. The worker will need `AddAi` (and so its own `Ai:ApiKey`), and `Document.AddChunks` on
a loaded document is already proven to insert (step-06 §5.1).
