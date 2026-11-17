using Microsoft.AspNetCore.HttpOverrides;
using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Platform.Http;

public static class ForwardedClientAddresses
{
    // The first middleware of the pipeline. With trusted proxies configured, a request whose
    // connection comes from one of them takes its client address from X-Forwarded-For and its
    // scheme from X-Forwarded-Proto, at most ForwardLimit entries from the right. Any other
    // request keeps its connection's address. Without trusted proxies nothing is added, so a
    // forwarded header never counts.
    public static IApplicationBuilder UseForwardedClientAddresses(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ApplicationServices.GetRequiredService<PlatformOptions>().Http;
        if (!options.TrustsProxies)
        {
            return app;
        }

        return app.UseForwardedHeaders(CreateOptions(options));
    }

    internal static ForwardedHeadersOptions CreateOptions(HttpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = options.ForwardLimit
        };

        // The defaults trust loopback; only the configured proxies may be trusted.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        foreach (var proxy in options.TrustedProxies)
        {
            forwarded.KnownProxies.Add(proxy);
        }

        foreach (var network in options.TrustedNetworks)
        {
            forwarded.KnownIPNetworks.Add(network);
        }

        return forwarded;
    }
}
