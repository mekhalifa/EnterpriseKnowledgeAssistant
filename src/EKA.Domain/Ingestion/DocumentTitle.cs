using EKA.Domain.Common;

namespace EKA.Domain.Ingestion;

/// <summary>
/// A document's display name: non-empty, trimmed, bounded in length.
/// </summary>
/// <remarks>
/// A sealed record rather than a struct, deliberately. The payload is a
/// string that must never be null, and a struct's implicit parameterless constructor would
/// let <c>default(DocumentTitle)</c> produce an instance whose <c>Value</c> is null while
/// the type claims it is non-nullable — a <see cref="NullReferenceException"/> waiting in
/// whatever code trusts the type. A class has no default instance, so the only way to get
/// one is through the constructor, which validates.
/// </remarks>
public sealed record DocumentTitle
{
    public const int MaxLength = 500;

    public DocumentTitle(string value)
    {
        // Guard FIRST, assign after. The original codebase's P0 bug was the reverse:
        // validation ran before the field was set, so every construction failed.
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Document title cannot be empty or whitespace.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new DomainException(
                $"Document title cannot exceed {MaxLength} characters; got {trimmed.Length}.");
        }

        Value = trimmed;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
