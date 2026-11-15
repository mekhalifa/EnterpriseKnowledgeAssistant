using EKA.Domain.Common;

namespace EKA.Application.Documents.UploadDocument;

/// <param name="Content">The file's bytes. Must be <b>seekable</b>: the handler reads the
/// header to detect the type, then rewinds to store it. ASP.NET Core's form files are
/// buffered and seekable. A raw network stream would need wrapping first.</param>
public sealed record UploadDocumentCommand(
    TenantId TenantId,
    string FileName,
    Stream Content,
    string? Title,
    int? Authority);

/// <param name="Queued">Whether the hand-off to the worker succeeded. When false the document is
/// still safely recorded as Pending, and the stalled-document sweep will enqueue it. Not an
/// error the client needs to act on.</param>
public sealed record UploadDocumentOutcome(
    Contracts.Documents.DocumentResponse Document,
    bool AlreadyRegistered,
    bool Queued);
