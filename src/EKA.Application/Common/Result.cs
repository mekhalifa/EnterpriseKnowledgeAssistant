namespace EKA.Application.Common;

/// <summary>
/// The outcome of a use case that either succeeded or failed for an expected reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just throw.</b> "Document not found" is not exceptional — it is one of two
/// normal outcomes of a lookup. Modelling it as an exception costs a stack capture on a
/// routine path, hides the failure from the method signature, and leaves every caller
/// guessing what might come flying out. A <c>Result</c> puts the failure in the return type
/// where the compiler and the reader can both see it.
/// </para>
/// <para>
/// <b>What still throws:</b> a broken invariant (<c>DomainException</c>), a missing
/// connection string, a bug. Those are not expected outcomes and should crash loudly rather
/// than be politely returned. See docs/adr/0011-result-pattern.md.
/// </para>
/// <para>
/// <b>The struct trap, stated plainly.</b> C# gives every struct an implicit parameterless
/// constructor, so <c>default(Result)</c> exists and bypasses the factory methods below. It
/// is neither a success nor a well-formed failure. Rather than let that masquerade as a
/// failure with a null error, <see cref="Error"/> throws and says why. This is the same
/// hazard <c>TenantId</c> documents, handled the same way: the type cannot prevent the
/// default, so it refuses to pretend the default is meaningful.
/// </para>
/// </remarks>
public readonly record struct Result
{
    private readonly Error? _error;

    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>The failure. Throws when the result succeeded, or was obtained via
    /// <c>default</c>, because reading it in either case is a bug in the caller.</summary>
    public Error Error => _error ?? throw new InvalidOperationException(
        IsSuccess
            ? "A successful result has no error."
            : "This Result was created with `default`, bypassing Success() and Failure(). "
              + "It carries no error to report.");

    public static Result Success() => new(true, null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result(false, error);
    }

    /// <summary>Lets a handler write <c>return error;</c> instead of the ceremony.</summary>
    public static implicit operator Result(Error error) => Failure(error);

    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}

/// <summary>
/// The outcome of a use case that produces a value when it succeeds.
/// </summary>
/// <typeparam name="TValue">What the use case returns on success.</typeparam>
public readonly record struct Result<TValue>
{
    private readonly TValue? _value;
    private readonly Error? _error;

    private Result(bool isSuccess, TValue? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// The produced value. Throws on a failed result rather than returning
    /// <c>default</c> — a silent null here would reappear as a
    /// <see cref="NullReferenceException"/> several frames away, with nothing left to point
    /// at the branch that was never checked.
    /// </summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            "A failed result has no value. Check IsSuccess before reading Value.");

    /// <inheritdoc cref="Result.Error"/>
    public Error Error => _error ?? throw new InvalidOperationException(
        IsSuccess
            ? "A successful result has no error."
            : "This Result was created with `default`, bypassing Success() and Failure(). "
              + "It carries no error to report.");

    public static Result<TValue> Success(TValue value) => new(true, value, null);

    public static Result<TValue> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result<TValue>(false, default, error);
    }

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure(error);

    public override string ToString() => IsSuccess ? $"Success({Value})" : $"Failure({Error})";
}
