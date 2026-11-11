using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Mmorpg.GameService.Content;

// A fixed window per client address, shared by the manifest and the file routes. A client
// start needs the manifest and about seven files, so the default of 120 requests a minute
// leaves room for many starts behind one address while bounding anonymous traffic.
internal sealed class ContentRateLimiterPolicy : IRateLimiterPolicy<string>
{
    public const string PolicyName = "content";

    private readonly ContentModuleOptions options;

    public ContentRateLimiterPolicy(ContentModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        OnRejected = RejectAsync;
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected { get; }

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{PolicyName}:{remoteAddress}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RateLimitPermitLimit,
                Window = options.RateLimitWindow,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining)
            ? remaining
            : options.RateLimitWindow;
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too Many Requests",
                detail: "Too many content requests. Try again later.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "rate_limit_exceeded"
                })
            .ExecuteAsync(context.HttpContext);
    }
}
