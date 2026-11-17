using System.Threading.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace EKA.Api.Security;

/// <summary>The Redis connection behind the shared rate-limit buckets (Step 14, ADR-0036).</summary>
internal static class SharedRateLimitStore
{
    /// <summary>
    /// Connects lazily: <c>AbortOnConnectFail=false</c> lets the API start while Redis is down,
    /// and the multiplexer keeps reconnecting in the background. A rate-limit store is not worth
    /// refusing to start over, because the limiter degrades without it (see
    /// <see cref="FallbackRateLimiter"/>).
    /// </summary>
    public static IConnectionMultiplexer? Connect(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        // A rate-limit check sits in front of every ask. If Redis is slow, falling back after
        // half a second costs less than holding each request for the 5 s default.
        options.AsyncTimeout = 500;
        options.SyncTimeout = 500;
        options.ClientName = "eka-api";

        return ConnectionMultiplexer.Connect(options);
    }
}

/// <summary>Readiness detail for Redis: PING, nothing else.</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "Redis is unreachable; rate limits are per instance until it returns.",
                exception);
        }
    }
}

/// <summary>
/// A rate limiter that asks Redis and, when Redis cannot answer, asks a per-process bucket.
/// </summary>
/// <remarks>
/// <para>
/// <b>Graceful degradation, chosen on purpose.</b> When the shared store fails there are three
/// possible answers. <i>Fail closed</i> (refuse every limited request) turns a cache outage
/// into "nobody can ask a question". <i>Fail open</i> (allow everything) removes the cost
/// guard exactly when something is already wrong. <i>Fall back</i> keeps a limit, only a
/// looser one: each of N instances grants the full rate, so a user gets at most N times their
/// allowance until Redis returns, and the per-tenant budget (ADR-0032) still caps spending.
/// </para>
/// <para>
/// A disconnected multiplexer goes straight to the fallback rather than waiting for a timeout
/// on every request. A failure that happens mid-call (timeout, connection reset) falls back for
/// that request. Either way the fallback is logged at most once a minute, so an outage is
/// visible without one log line per request.
/// </para>
/// </remarks>
internal sealed partial class FallbackRateLimiter(
    IConnectionMultiplexer redis,
    RateLimiter shared,
    RateLimiter local,
    ILogger<FallbackRateLimiter> logger) : RateLimiter
{
    private static long _lastWarningTicks;

    public override TimeSpan? IdleDuration =>
        shared.IdleDuration is { } a && local.IdleDuration is { } b ? (a < b ? a : b) : null;

    public override RateLimiterStatistics? GetStatistics() => shared.GetStatistics();

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        if (!redis.IsConnected)
        {
            return Fallback(permitCount, cause: null);
        }

        try
        {
            return shared.AttemptAcquire(permitCount);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            return Fallback(permitCount, exception);
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        if (!redis.IsConnected)
        {
            return Fallback(permitCount, cause: null);
        }

        try
        {
            return await shared.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
        {
            return Fallback(permitCount, exception);
        }
    }

    private RateLimitLease Fallback(int permitCount, Exception? cause)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
        {
            LogFallingBack(logger, cause);
        }

        return local.AttemptAcquire(permitCount);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            shared.Dispose();
            local.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await shared.DisposeAsync().ConfigureAwait(false);
        await local.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate-limit store (Redis) unavailable; using per-instance limits until it returns")]
    private static partial void LogFallingBack(ILogger logger, Exception? exception);
}
