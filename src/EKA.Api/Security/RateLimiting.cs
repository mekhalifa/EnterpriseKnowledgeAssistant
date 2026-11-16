using System.Globalization;
using System.Threading.RateLimiting;

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

        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitingOptions.AskPolicy, context => Bucket(context, RateLimitingOptions.AskPolicy, limits.Ask));
            options.AddPolicy(RateLimitingOptions.SearchPolicy, context => Bucket(context, RateLimitingOptions.SearchPolicy, limits.Search));
            options.AddPolicy(RateLimitingOptions.UploadPolicy, context => Bucket(context, RateLimitingOptions.UploadPolicy, limits.Upload));

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
    private static RateLimitPartition<string> Bucket(HttpContext context, string policy, TokenBucketOptions bucket)
    {
        var user = context.User;
        var key = $"{policy}:{user.FindFirst(JwtAuthentication.TenantClaim)?.Value}:{user.FindFirst("sub")?.Value}";

        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = bucket.TokenLimit,
            TokensPerPeriod = bucket.TokensPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            // No queueing: a request over the limit is answered now, not held open.
            QueueLimit = 0,
        });
    }
}
