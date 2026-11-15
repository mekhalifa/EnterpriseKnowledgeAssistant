# ADR-0023: Extracted text is a versioned JSON artifact in file storage

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

Between extraction (Step 7) and chunking (Step 8) the text has to live somewhere. It can be
megabytes per document. It is written once per extraction and read once by the chunker,
which then stores the text again as chunks. `Document` has no property for it.

## Decision

- Stored through `IFileStorage` at `<StorageKey>.extracted.json`, by `ExtractedTextStore`.
- Its shape is `{ formatVersion, documentId, contentHash, extractedAt, sections: [{ pageNumber,
  sectionPath, text }] }`. `formatVersion` (currently 1) lets a later reader refuse or
  migrate an older artifact. `contentHash` ties it to the exact bytes it came from.
- **Written before the status changes to `Chunking`**, so a `Chunking` document always has
  its text. Re-extraction replaces the artifact whole, and deleting the document deletes it.

## Consequences

### Positive

- The database holds no large text twice. Re-running extraction is idempotent with no
  cleanup.
- Step 8 reads one file per document, with page and section metadata already attached.

### Negative / costs we accept

- Text is not queryable in SQL, so there is no "search extracted text" until vectors exist.
- Two stores must agree: the row says Chunking, and the artifact must exist. The write order
  guarantees it on success. A storage loss breaks it, and is detected when the chunker
  fails to read.

## Alternatives considered

- **A `DocumentPages` table.** Transactional with the status change, and queryable. It wins
  if extracted text must be searched or edited before chunking. It costs storing the text in
  SQL twice.
- **Chunking immediately inside extraction.** No intermediate store at all. It wins if
  chunking settings never change. It loses re-chunking without re-parsing, which Step 11's
  tuning will want.

## Revisit when

- Extracted text needs to be shown or edited in the UI (Step 13).
- Chunking parameters change often enough that re-extraction costs matter.
