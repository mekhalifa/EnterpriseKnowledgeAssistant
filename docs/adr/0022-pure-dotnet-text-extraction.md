# ADR-0022: Pure .NET text extraction (PdfPig, Open XML SDK), with detection from content

- **Status:** Accepted
- **Date:** 2026-11-15
- **Deciders:** project owner

## Context

Ingestion must turn PDF, Word, text and Markdown into text, keeping page numbers and section
headings for citations (Step 10). Uploaded files are untrusted: their name and declared
content type are whatever the client says.

## Decision

- **Formats:** PDF via PdfPig 0.1.16 (Apache-2.0), one section per page, text in
  `ContentOrderTextExtractor` order. DOCX via DocumentFormat.OpenXml 3.5.1 (MIT), one section
  per built-in heading (`Title`, `Heading1`–`Heading9`) with the heading trail as the path.
  `.txt` and `.md` are read as UTF-8 and split at ATX headings.
- **Chain of Responsibility:** each `ITextExtractor` claims a `DocumentFileType`, and
  `TextExtractionPipeline` picks the first that does. Normalization (line endings, control
  characters, blank runs) happens **once**, in the pipeline. A document with no text after
  normalization is unreadable.
- **The bytes decide the type.** `FileTypeDetector` reads the first 512 bytes: `%PDF-`, a ZIP
  header, or valid UTF-8 without NUL bytes. The extension must agree, or the upload is
  refused. The stored content type is the detected one, never the claimed one.
- **Parser failures are permanent** (`UnreadableDocumentException` → document `Failed`).
  I/O errors and cancellation are transient, with one exception: a truncated file
  (end-of-stream) counts as permanent.

## Consequences

### Positive

- Offline, free, no third party sees the documents, and permissive licences.
- A renamed executable or a mislabelled file is refused before it is stored.
- Adding a format means one class and one registration.

### Negative / costs we accept

- **No OCR.** Scanned PDFs fail with a message saying so.
- **No layout analysis.** Multi-column pages and tables may come out in surprising order.
- DOCX headings must use Word's heading styles. Bold-as-heading yields one section.
- A ZIP header named `.docx` passes detection. The extractor is the second check, and only
  when the worker runs.
- Parsing untrusted files is attack surface, though in the worker process, not the API.

## Alternatives considered

- **Azure AI Document Intelligence.** Layout, tables, OCR and reading order. It wins when
  scanned documents or complex layouts matter, as Step 11's evaluation may show. It is paid
  per page and adds a data processor.
- **Apache Tika (as a container).** Hundreds of formats. It wins if format breadth matters
  more than depth. It brings a JVM service and its own operational cost.
- **Trusting the client's Content-Type.** Simpler. It is never right for untrusted input.

## Revisit when

- Step 11 shows retrieval failing on scanned or multi-column documents.
- A format beyond these four is required.
