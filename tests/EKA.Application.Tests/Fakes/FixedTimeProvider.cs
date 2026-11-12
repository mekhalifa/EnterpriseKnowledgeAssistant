namespace EKA.Application.Tests.Fakes;

/// <summary>
/// A clock that does not move.
/// </summary>
/// <remarks>
/// <para>
/// Ten lines instead of a NuGet package. <see cref="TimeProvider"/> is an abstract class
/// with a virtual <see cref="GetUtcNow"/>, so substituting it needs no library — and
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> ships on a different version line from
/// the rest of the Microsoft packages here, which would be a standing upgrade hazard in
/// exchange for these ten lines.
/// </para>
/// <para>
/// This is the test-side half of ADR-0008. Because the domain takes instants as parameters
/// and the application layer supplies them from an injected provider, a test can assert
/// <c>CreatedAt == 2026-01-01T00:00:00Z</c> exactly, rather than "within a second of now" —
/// which is the kind of assertion that passes on a laptop and fails on a loaded CI agent.
/// </para>
/// </remarks>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>An arbitrary, memorable instant for tests with no opinion about when.</summary>
    public static readonly DateTimeOffset DefaultNow =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static FixedTimeProvider Default => new(DefaultNow);

    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Moves the clock forward, for a test that needs two distinct instants.</summary>
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
