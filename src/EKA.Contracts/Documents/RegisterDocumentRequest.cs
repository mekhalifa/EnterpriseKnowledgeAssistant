namespace EKA.Contracts.Documents;

/// <summary>
/// Registers a document's metadata so the ingestion pipeline can pick it up.
/// </summary>
/// <param name="Title">Display name. Trimmed and length-checked by the domain.</param>
/// <param name="FileName">Original file name as uploaded.</param>
/// <param name="ContentType">MIME type, for example <c>application/pdf</c>.</param>
/// <param name="SizeBytes">File size in bytes. Must be positive.</param>
/// <param name="ContentHash">
/// Lowercase hex SHA-256 of the file's bytes. This is the idempotency key: registering the
/// same bytes twice returns the first document rather than creating a second.
/// </param>
/// <param name="Authority">
/// Optional trust score from 0 to 100 used to break ties during retrieval. Defaults to 50
/// (<c>Standard</c>) when omitted.
/// </param>
/// <remarks>
/// <para>
/// <b>What this request deliberately does not contain.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>The file bytes.</b> Step 7 adds multipart upload, a storage abstraction and size
/// limits. Step 4 registers metadata only, which is enough to exercise the full
/// request → validate → handle → persist → respond path that the rest of the API is built
/// on.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A storage key.</b> The server decides where bytes live. A client-supplied path is a
/// directory-traversal and overwrite vector — <c>../../other-tenant/secret.pdf</c> — and no
/// amount of sanitising is as safe as never accepting it.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A tenant id.</b> It comes from the caller's authenticated identity. A tenant id in a
/// request body is a tenant id an attacker can change.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed record RegisterDocumentRequest(
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ContentHash,
    int? Authority = null);
