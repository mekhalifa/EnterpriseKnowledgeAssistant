namespace EKA.Application.Common;

/// <summary>
/// The category of an expected failure. Decides the HTTP status the API returns.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately small. Every value here must map to exactly one HTTP status code, and that
/// mapping lives in one place in <c>EKA.Api</c>. If a new value cannot answer "which status
/// code?" in one word, it does not belong in this enum — it belongs in the
/// <see cref="Error.Code"/> string, which is free-form.
/// </para>
/// <para>
/// Explicit numeric values because these may end up in logs and metrics labels, where a
/// silently renumbered enum makes historical data lie.
/// </para>
/// </remarks>
public enum ErrorType
{
    /// <summary>The request was understood but its content is not acceptable. HTTP 400.</summary>
    /// <remarks>
    /// Reserved for rules that need state to evaluate — "this storage key is already in
    /// use". Pure shape checks (required, length, range) never reach a handler: they are
    /// rejected by the validation filter at the edge.
    /// </remarks>
    Validation = 1,

    /// <summary>The addressed resource does not exist, or not for this caller. HTTP 404.</summary>
    /// <remarks>
    /// Also returned when a record exists but belongs to another tenant. Distinguishing
    /// "not yours" from "not there" would confirm the existence of another tenant's data,
    /// which is an information leak even without the content.
    /// </remarks>
    NotFound = 2,

    /// <summary>The request conflicts with current state. HTTP 409.</summary>
    /// <remarks>
    /// The natural home for an illegal lifecycle move — resetting a document that never
    /// failed — and for a uniqueness collision discovered at write time.
    /// </remarks>
    Conflict = 3,

    /// <summary>The caller is authenticated but not allowed to do this. HTTP 403.</summary>
    Forbidden = 4,

    /// <summary>An external dependency failed. HTTP 502.</summary>
    /// <remarks>
    /// Used from Step 5 onward for AI provider and vector store failures. It is separate
    /// from a 500 because the fault is not ours and the correct client behaviour differs:
    /// a provider timeout is worth retrying, a bug in our code is not.
    /// </remarks>
    Provider = 5,
}
