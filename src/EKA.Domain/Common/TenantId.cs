namespace EKA.Domain.Common;

/// <summary>
/// Identifies the customer or organizational boundary that owns a record. Every document,
/// chunk, conversation, and user carries one, and every query filters on it.
/// </summary>
/// <remarks>
/// <b>Struct caveat you must know:</b> C# gives every struct an implicit parameterless
/// constructor that this type's validation cannot intercept, so <c>default(TenantId)</c>
/// and <c>new TenantId()</c> both produce a value wrapping <see cref="Guid.Empty"/>
/// without ever running the guard below. That is why <see cref="IsEmpty"/> exists and why
/// aggregates check it in their constructors rather than trusting the type alone.
/// <para>
/// A struct is still the right choice here: tenant ids are copied constantly and stored on
/// every row, so the allocation a class would cost is not worth paying for a guarantee the
/// language cannot give us anyway.
/// </para>
/// </remarks>
public readonly record struct TenantId
{
    public TenantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainException("Tenant id cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    /// <summary>True for a value that bypassed validation via <c>default</c>.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    public static TenantId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
