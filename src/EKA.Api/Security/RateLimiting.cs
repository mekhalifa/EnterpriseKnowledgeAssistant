using System.Globalization;
using System.Threading.RateLimiting;
using EKA.Application.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RedisRateLimiting;
using StackExchange.Redis;

namespace EKA.Api.Security;

/// <summary>One token bucket: a burst of <see cref="TokenLimit"/>, refilled at <see cref="TokensPerMinute"/>.</summary>
public sealed class TokenBucketOptions
{
    public int TokenLimit { get; set; }

    public int TokensPerMinute { get; set; }
}

/// <summary>The <c>RateLimiting</c> section (Step 12, ADR-0032).</summary>
/// <remarks>
/// <para>
/// <b>Per person, not per server.</b> Each bucket is partitioned by tenant and subject, so one
/// busy user cannot slow down a colleague, and a tenant cannot exhaust another's allowance.
/// </para>
/// <para>
/// <b>Why these three.</b> They are the endpoints that cost money or heavy work: an ask is an
/// embedding and a chat completion, a search is an embedding, an upload is parsing and
/// embedding in the worker. Reads of stored data (lists, conversation history) cost a query
/// and are not limited here.
/// </para>
/// <para>
/// A token bucket rather than a fixed window: it allows a natural burst (a few quick
/// follow-ups) without permitting a sustained flood, and it has no window edge where twice the
/// limit fits into a second.
/// </para>
/// </remarks>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public const string AskPolicy = "ask";
    public const string SearchPolicy = "search";
    public const string UploadPolicy = "upload";

    public TokenBucketOptions Ask { get; set; } = new() { TokenLimit = 5, TokensPerMinute = 10 };

    public TokenBucketOptions Search { get; set; } = new() { TokenLimit = 10, TokensPerMinute = 30 };

    public TokenBucketOptions Upload { get; set; } = new() { TokenLimit = 5, TokensPerMinute = 10 };
}

internal static class RateLimiting
{
    /// <summary>The connection string that turns on the shared store. Absent: per process.</summary>
    public const string RedisConnectionName = "Redis";

    public static void AddEkaRateLimiting(this WebApplicationBuilder builder)
    {
        var limits = builder.Configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
            ?? new RateLimitingOptions();

        foreach (var (name, bucket) in new[] { ("Ask", limits.Ask), ("Search", limits.Search), ("Upload", limits.Upload) })
        {
            if (bucket.TokenLimit < 1 || bucket.TokensPerMinute < 1)
            {
                throw new InvalidOperationException(
                    $"'{RateLimitingOptions.SectionName}:{name}' needs TokenLimit and TokensPerMinute of at least 1.");
            }
        }

        // Step 14 (ADR-0036): with a Redis connection string, the buckets live in Redis and every
        // API instance draws on the same one per user. Without it, each process keeps its own,
        // which is right for one instance and for the test suite.
        var redis = SharedRateLimitStore.Connect(builder.Configuration.GetConnectionString(RedisConnectionName));
        if (redis is not null)
        {
            builder.Services.AddSingleton(redis);
            builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                "redis",
                _ => new RedisHealthCheck(redis),
                // Degraded, not Unhealthy: the limiter falls back to per-process buckets, so the
                // instance can still serve. Readiness must fail only for what it cannot do without.
                HealthStatus.Degraded,
                [HealthTags.Ready],
                TimeSpan.FromSeconds(3)));
        }

        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitingOptions.AskPolicy, context => Bucket(context, RateLimitingOptions.AskPolicy, limits.Ask, redis));
            options.AddPolicy(RateLimitingOptions.SearchPolicy, context => Bucket(context, RateLimitingOptions.SearchPolicy, limits.Search, redis));
            options.AddPolicy(RateLimitingOptions.UploadPolicy, context => Bucket(context, RateLimitingOptions.UploadPolicy, limits.Upload, redis));

            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many requests",
                        detail: "You are sending requests faster than this service allows. Wait and try again.",
                        extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" })
                    .ExecuteAsync(rejected.HttpContext)
                    .ConfigureAwait(false);
            };
        });
    }

    /// <summary>
    /// One bucket per tenant and subject. An unauthenticated request never reaches here: the
    /// limiter runs after authorization, so anonymous traffic is answered 401 without consuming
    /// anyone's allowance.
    /// </summary>
    private static RateLimitPartition<string> Bucket(HttpContext context, string policy, TokenBucketOptions bucket, IConnectionMultiplexer? redis)
    {
        var user = context.User;
        var key = $"{policy}:{user.FindFirst(JwtAuthentication.TenantClaim)?.Value}:{user.FindFirst("sub")?.Value}";

        if (redis is null)
        {
            return RateLimitPartition.GetTokenBucketLimiter(key, _ => InMemory(bucket));
        }

        var logger = context.RequestServices.GetRequiredService<ILogger<FallbackRateLimiter>>();

        return RateLimitPartition.Get(key, partitionKey => new FallbackRateLimiter(
            redis,
            new RedisTokenBucketRateLimiter<string>($"eka:ratelimit:{partitionKey}", new RedisTokenBucketRateLimiterOptions
            {
                ConnectionMultiplexerFactory = () => redis,
                TokenLimit = bucket.TokenLimit,
                TokensPerPeriod = bucket.TokensPerMinute,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            }),
            new TokenBucketRateLimiter(InMemory(bucket)),
            logger));
    }

    private static TokenBucketRateLimiterOptions InMemory(TokenBucketOptions bucket) => new()
    {
        TokenLimit = bucket.TokenLimit,
        TokensPerPeriod = bucket.TokensPerMinute,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        AutoReplenishment = true,
        // No queueing: a request over the limit is answered now, not held open.
        QueueLimit = 0,
    };
}
