using System.Diagnostics.CodeAnalysis;
using EKA.Application.Common;
using EKA.Domain.Access;
using EKA.Domain.Ingestion;

namespace EKA.Application.Documents;

/// <summary>
/// The two questions every document use case asks, answered once (Step 12).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>May the caller see it?</b> Read access, ownership, or administration.
/// If not: 404, indistinguishable from "does not exist".</description></item>
/// <item><description><b>May the caller change it?</b> Owner or administrator. If they can
/// see it but not change it: 403.</description></item>
/// </list>
/// The rules themselves live on the aggregate (<see cref="Document.IsVisibleTo"/>,
/// <see cref="Document.CanBeManagedBy"/>). This only turns them into the right error, so no
/// handler can pick the wrong status for the same situation.
/// </remarks>
internal static class DocumentAuthorization
{
    /// <returns>True, with the error to return, when the caller may not see the document.</returns>
    public static bool CannotSee(
        [NotNullWhen(false)] Document? document,
        AccessScope caller,
        Guid documentId,
        [NotNullWhen(true)] out Error? error)
    {
        error = document is not null && document.IsVisibleTo(caller) ? null : DocumentErrors.NotFound(documentId);
        return error is not null;
    }

    /// <returns>True, with the error to return, when the caller may not change the document.</returns>
    public static bool CannotManage(
        [NotNullWhen(false)] Document? document,
        AccessScope caller,
        Guid documentId,
        [NotNullWhen(true)] out Error? error)
    {
        if (CannotSee(document, caller, documentId, out error))
        {
            return true;
        }

        error = document.CanBeManagedBy(caller) ? null : DocumentErrors.NotManageable(documentId);
        return error is not null;
    }
}
