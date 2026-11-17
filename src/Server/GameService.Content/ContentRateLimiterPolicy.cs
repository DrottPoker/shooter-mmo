using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Content;

// A fixed window per client address, shared by the manifest and the file routes. A client
// start needs the manifest and about seven files, so the default of 120 requests a minute
// leaves room for many starts behind one address while bounding anonymous traffic.
internal static class ContentRateLimiterPolicy
{
    public const string PolicyName = "content";

    public static FixedWindowRateLimitPolicy Create(ContentModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new FixedWindowRateLimitPolicy(
            PolicyName,
            new FixedWindowRateLimit(options.RateLimitPermitLimit, options.RateLimitWindow),
            ClientAddress.PartitionKey,
            "Too many content requests. Try again later.");
    }
}
