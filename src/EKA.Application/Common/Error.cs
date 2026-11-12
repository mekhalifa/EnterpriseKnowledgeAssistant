namespace EKA.Application.Common;

/// <summary>
/// A failure a use case is expected to produce, described well enough for both a human and
/// a client program.
/// </summary>
/// <param name="Code">
/// A stable, machine-readable identifier such as <c>document.not_found</c>. Clients branch
/// on this; it is part of the API contract and may not be changed casually.
/// </param>
/// <param name="Message">
/// Human-readable detail. Safe to show a user, which means it must never contain a
/// connection string, a stack trace, or another tenant's data.
/// </param>
/// <param name="Type">The category, which decides the HTTP status.</param>
/// <remarks>
/// <para>
/// <b>Two fields, not one.</b> <see cref="Type"/> answers "what status code?" and
/// <see cref="Code"/> answers "which failure exactly?". Collapsing them would force either
/// a status-code switch over dozens of string literals in the API layer, or a client that
/// cannot tell two different 404s apart.
/// </para>
/// <para>
/// A <c>sealed record</c> rather than a struct, for the same reason <c>DocumentTitle</c> is:
/// the payload is non-null strings, and a struct's implicit parameterless constructor would
/// hand out an instance whose <c>Code</c> is null while the type promises otherwise.
/// </para>
/// </remarks>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>
    /// The sentinel carried by a successful <see cref="Result"/>. Never surfaced to a
    /// client; it exists so <c>Result.Success()</c> does not need a nullable error field
    /// that every reader has to reason about.
    /// </summary>
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Validation);

    public static Error Validation(string code, string message) =>
        new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorType.Forbidden);

    public static Error Provider(string code, string message) =>
        new(code, message, ErrorType.Provider);

    public override string ToString() => $"{Code}: {Message}";
}
