using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Platform.Tests;

// The public limit as the platform registers it: per client address, the shared rejection
// shape, and a window that resets.
public sealed class FixedWindowRateLimitPolicyTests
{
    private const string FirstClient = "203.0.113.10";
    private const string SecondClient = "203.0.113.11";

    [Fact]
    public async Task ThePublicLimitCountsPerAddressRejectsAlikeAndResets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var application = await StartAsync(new FixedWindowRateLimit(2, TimeSpan.FromSeconds(1)));
        using var client = CreateClient(application);

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, FirstClient));
        using (var rejected = await SendAsync(client, FirstClient))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter?.Delta);
            using var problem = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal("rate_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(429, problem.RootElement.GetProperty("status").GetInt32());
        }

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, SecondClient));

        await Task.Delay(TimeSpan.FromMilliseconds(1_200), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, FirstClient));
    }

    [Fact]
    public void ThePublicLimitIsOptionalAndBounded()
    {
        var errors = new List<string>();
        var defaults = FixedWindowRateLimit.Read(
            new ConfigurationBuilder().Build(),
            PublicRateLimitPolicy.Section,
            PublicRateLimitPolicy.Defaults,
            PublicRateLimitPolicy.MaximumPermitLimit,
            errors);
        FixedWindowRateLimit.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:Public:PermitLimit"] = "100001",
                    ["RateLimiting:Public:WindowSeconds"] = "0"
                })
                .Build(),
            PublicRateLimitPolicy.Section,
            PublicRateLimitPolicy.Defaults,
            PublicRateLimitPolicy.MaximumPermitLimit,
            errors);

        Assert.Equal(new FixedWindowRateLimit(300, TimeSpan.FromSeconds(60)), defaults);
        Assert.Equal(
            [
                "RateLimiting:Public:PermitLimit must be an integer from 1 to 100000.",
                "RateLimiting:Public:WindowSeconds must be an integer from 1 to 3600."
            ],
            errors);
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string forwardedFor)
    {
        using var response = await SendAsync(client, forwardedFor);
        return response.StatusCode;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/public");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<WebApplication> StartAsync(FixedWindowRateLimit limit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddGameServicePlatform(new PlatformOptions(
            "Host=127.0.0.1;Database=test;Username=test;Password=test",
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1),
            false)
        {
            Http = HttpServerOptions.Default with { TrustedProxies = [IPAddress.Loopback] },
            PublicRateLimit = limit
        });
        // The retention host and the drain are not part of this test.
        foreach (var hosted in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                     .ToArray())
        {
            builder.Services.Remove(hosted);
        }

        var application = builder.Build();
        application.UseForwardedClientAddresses();
        application.UseApiPipeline();
        application.UseRateLimiter();
        application.MapGet("/public", () => Results.Ok()).RequireRateLimiting(PublicRateLimitPolicy.PolicyName);
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private static HttpClient CreateClient(WebApplication application)
    {
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
