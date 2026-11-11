namespace EKA.Domain.Common;

/// <summary>
/// Base class for entities: things with an identity that survives changes to their values.
/// A <c>Document</c> whose title is corrected is still the same document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity, not values.</b> Equality compares the concrete type and <see cref="Id"/>.
/// This is the line between an entity and a value object: two <c>DocumentTitle</c> values
/// are equal when their text matches, but two <c>Document</c> objects are equal only when
/// they are the same document.
/// </para>
/// <para>
/// <b>Id lives here and only here.</b> A derived class that declares its own <c>Id</c>
/// shadows this one, leaving two properties where the persistence layer maps one and the
/// rest of the code reads the other. If a derived entity appears to need its own Id, it
/// needs a different base class instead.
/// </para>
/// <para>
/// <b>Time is a parameter, never a clock read.</b> There is deliberately no
/// <c>DateTimeOffset.UtcNow</c> in this class. Methods that record when something happened
/// take the instant from the caller, so a test can assert on an exact timestamp instead of
/// comparing against "roughly now". See docs/adr/0008-deterministic-time-in-domain.md.
/// </para>
/// </remarks>
/// <typeparam name="TId">The identity type. A struct, so an entity can never have a null id.</typeparam>
public abstract class EntityAudit<TId>
    where TId : struct, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>Required by EF Core materialization and by serializers, which construct
    /// an instance before assigning state. Not for application code to call.</summary>
    protected EntityAudit()
    {
    }

    protected EntityAudit(TId id, DateTimeOffset createdAt)
    {
        if (id.Equals(default))
        {
            throw new DomainException($"{GetType().Name} requires a non-default id.");
        }

        Id = id;
        CreatedAt = createdAt;
    }

    public TId Id { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? ModifiedAt { get; protected set; }

    /// <summary>Set by the persistence layer from the ambient user, not by the domain —
    /// the domain has no notion of "who is logged in".</summary>
    public string? CreatedBy { get; protected set; }

    public string? ModifiedBy { get; protected set; }

    /// <summary>
    /// Events raised since this instance was loaded. Dispatched and cleared by the
    /// persistence layer after a successful commit.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    protected void MarkModified(DateTimeOffset at) => ModifiedAt = at;

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        // Two different entity types are never equal, even with matching ids — a Document
        // and a Conversation that happen to share a Guid are not the same thing.
        if (obj is not EntityAudit<TId> other || GetType() != other.GetType())
        {
            return false;
        }

        // A not-yet-persisted entity (default id) is equal only to itself, which the
        // ReferenceEquals check above already handled.
        return !Id.Equals(default) && Id.Equals(other.Id);
    }

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(EntityAudit<TId>? left, EntityAudit<TId>? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(EntityAudit<TId>? left, EntityAudit<TId>? right) =>
        !(left == right);
}
