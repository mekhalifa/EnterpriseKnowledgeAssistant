namespace EKA.Contracts.Documents;

/// <summary>
/// The form fields that accompany an uploaded file (<c>multipart/form-data</c>).
/// </summary>
/// <remarks>
/// <para>
/// The file itself is a separate form part, bound by the endpoint. This project references
/// nothing, so it cannot name ASP.NET Core's <c>IFormFile</c>, and a contract that a
/// non-.NET client reads should not try to.
/// </para>
/// <para>
/// <b>What the client no longer sends,</b> compared with Step 4's metadata-only
/// registration: size, content type and content hash. The server measures all three from
/// the bytes it receives. A client-asserted hash was a client-asserted identity, so a
/// caller could have claimed another document's hash without ever holding its bytes.
/// </para>
/// </remarks>
/// <param name="Title">Display name. Defaults to the file name without its extension.</param>
/// <param name="Authority">Optional trust score from 0 to 100; defaults to 50 (Standard).</param>
public sealed record UploadDocumentMetadata(string? Title, int? Authority);
