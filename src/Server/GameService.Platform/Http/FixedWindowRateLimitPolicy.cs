using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Platform.Http;

// The permits of one fixed window and the window's length.
public sealed record FixedWindowRateLimit(int PermitLimit, TimeSpan Window)
{
    // Reads <section>:PermitLimit and <section>:WindowSeconds, each optional with its default.
    public static FixedWindowRateLimit Read(
        IConfiguration configuration,
        string section,
        FixedWindowRateLimit defaults,
        int maximumPermitLimit,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(errors);

        var permitLimit = ConfigurationValidation.ReadOptionalIntRange(
            configuration,
            $"{section}:PermitLimit",
            defaults.PermitLimit,
            1,
            maximumPermitLimit,
            errors);
        var windowSeconds = ConfigurationValidation.ReadOptionalIntRange(
            configuration,
            $"{section}:WindowSeconds",
            (int)defaults.Window.TotalSeconds,
            1,
            3_600,
            errors);
        return new FixedWindowRateLimit(permitLimit, TimeSpan.FromSeconds(windowSeconds));
    }
}

// A fixed window per partition, rejected the way every GameService limit is: 429 with the code
// rate_limit_exceeded in Problem Details and a Retry-After that counts down to the window's end.
// The partition key is prefixed with the policy name, so two policies never share a window.
public sealed class FixedWindowRateLimitPolicy : IRateLimiterPolicy<string>
{
    public const string RejectionCode = "rate_limit_exceeded";

    private readonly string name;
    private readonly FixedWindowRateLimit limit;
    private readonly Func<HttpContext, string> partitionKey;
    private readonly string rejectionDetail;

    public FixedWindowRateLimitPolicy(
        string name,
        FixedWindowRateLimit limit,
        Func<HttpContext, string> partitionKey,
        string rejectionDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectionDetail);
        this.name = name;
        this.limit = limit;
        this.partitionKey = partitionKey;
        this.rejectionDetail = rejectionDetail;
        OnRejected = RejectAsync;
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected { get; }

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{name}:{partitionKey(httpContext)}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    // Also used by the distributed authentication limit, so every limit answers alike.
    public static async Task WriteRejectionAsync(HttpContext context, TimeSpan retryAfter, string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = ServiceResultExtensions.FormatRetryAfter(retryAfter);
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too Many Requests",
                detail: detail,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = RejectionCode
                })
            .ExecuteAsync(context);
    }

    private async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
            ? remaining
            : limit.Window;
        await WriteRejectionAsync(context.HttpContext, retryAfter, rejectionDetail);
    }
}

// The limit of the anonymous routes that have no limit of their own: the shard list, the health
// routes, and the root redirect. It is counted per client address.
public static class PublicRateLimitPolicy
{
    public const string PolicyName = "public";
    public const string Section = "RateLimiting:Public";
    public const int MaximumPermitLimit = 100_000;

    public static FixedWindowRateLimit Defaults { get; } = new(300, TimeSpan.FromSeconds(60));
}
