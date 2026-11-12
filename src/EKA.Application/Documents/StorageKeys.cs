using EKA.Domain.Common;

namespace EKA.Application.Documents;

/// <summary>
/// Decides where a document's bytes will live.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server builds this, never the client.</b> A caller-supplied path is a
/// directory-traversal and overwrite vector: <c>../../other-tenant/policy.pdf</c> reads or
/// clobbers somebody else's file, and no amount of sanitising beats never accepting the
/// input. The key is derived entirely from values the server already trusts — the
/// authenticated tenant and an id this process generated.
/// </para>
/// <para>
/// The tenant id leads the key so that a storage account can be partitioned, lifecycle-ruled
/// or access-scoped per tenant later without rewriting every existing key.
/// </para>
/// <para>
/// Step 7 moves this behind the storage abstraction it introduces, where the backing store
/// (local disk, Azure Blob, S3) gets a say in the shape. It lives here now because the
/// rule it enforces — the client never names a path — must hold from the first endpoint,
/// not from the step that happens to add file uploads.
/// </para>
/// </remarks>
internal static class StorageKeys
{
    /// <summary>Longest extension kept. Anything longer is not a real file extension.</summary>
    private const int MaxExtensionLength = 10;

    public static string For(TenantId tenantId, Guid documentId, string fileName) =>
        $"{tenantId.Value:N}/{documentId:N}{SafeExtension(fileName)}";

    /// <summary>
    /// The file's extension, reduced to ASCII letters and digits, or empty.
    /// </summary>
    /// <remarks>
    /// Kept only because it helps a human browsing the bucket and lets a content-type
    /// sniffer agree with the stored name. It is cosmetic, so it is allowed to be empty and
    /// is never trusted — the authoritative content type is the one recorded on the
    /// document.
    /// </remarks>
    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (string.IsNullOrEmpty(extension) || extension.Length > MaxExtensionLength + 1)
        {
            return string.Empty;
        }

        // Skip the leading dot, then keep only characters that cannot mean anything to a
        // path parser. A filename of "report.pdf/../../etc" leaves nothing here.
        var cleaned = new string([.. extension[1..].Where(char.IsAsciiLetterOrDigit)]);

        return cleaned.Length == 0 ? string.Empty : $".{cleaned.ToLowerInvariant()}";
    }
}
