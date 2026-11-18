using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mmorpg.GameService.Config;

namespace Mmorpg.GameService.Tests;

// GameService behind a proxy, through the whole host pipeline. The test client connects from
// 127.0.0.1, so trusting 127.0.0.1 makes it a proxy on the same host, and X-Forwarded-For names
// the clients behind it.
public sealed class BehindAProxyTests
{
    private const string FirstClient = "203.0.113.20";
    private const string SecondClient = "203.0.113.21";

    [Fact]
    public async Task TheLoginLimitCountsEachClientBehindATrustedProxy()
    {
        var settings = Settings(trustedProxies: "127.0.0.1");
        settings["RateLimiting:Authentication:PermitLimit"] = "2";
        await using var application = await GameServiceHostSettings.StartAsync(settings);
        using var client = GameServiceHostSettings.CreateClient(application);

        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(client, SecondClient));
    }

    // A caller that is not a trusted proxy keeps its own address, so naming another client in
    // X-Forwarded-For neither escapes its limit nor fails the request.
    [Fact]
    public async Task AForwardedAddressFromAnUntrustedCallerIsIgnored()
    {
        var settings = Settings(trustedProxies: "10.0.0.1");
        settings["RateLimiting:Authentication:PermitLimit"] = "2";
        await using var application = await GameServiceHostSettings.StartAsync(settings);
        using var client = GameServiceHostSettings.CreateClient(application);

        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.BadRequest, await LoginAsync(client, SecondClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.22"));
    }

    // Behind a proxy on the same host the connection is loopback, but the client is not: the
    // development routes answer as for any remote caller.
    [Fact]
    public async Task ARemoteClientBehindASameHostProxyIsNotLoopback()
    {
        var settings = Settings(trustedProxies: "127.0.0.1");
        settings["DevelopmentSimulationBots:Enabled"] = "true";
        settings["ACTIVE_SIMULATION_BOTS_SECRET"] = GameServiceHostSettings.BotSecret;
        await using var application = await GameServiceHostSettings.StartAsync(settings);
        using var client = GameServiceHostSettings.CreateClient(application);

        var remote = await BotTicketAsync(client, FirstClient);
        var local = await BotTicketAsync(client, null);

        Assert.Equal((HttpStatusCode.Forbidden, "development_bot_loopback_required"), remote);
        Assert.Equal((HttpStatusCode.Unauthorized, "invalid_development_bot_authority"), local);
    }

    // The root redirect is a public route: it counts per client address, and answers 429 past its limit.
    [Fact]
    public async Task ThePublicRoutesCountPerClientAddress()
    {
        var settings = Settings(trustedProxies: "127.0.0.1");
        settings["RateLimiting:Public:PermitLimit"] = "2";
        await using var application = await GameServiceHostSettings.StartAsync(settings);
        using var client = CreateClientWithoutRedirects(application);

        Assert.Equal(HttpStatusCode.Redirect, await RootAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.Redirect, await RootAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RootAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.Redirect, await RootAsync(client, SecondClient));
    }

    // The proxy and the container ask for health every few seconds from one address, and a prober
    // that is answered 429 is taken for a service that is down. The health routes carry no limit, so
    // they answer as often as they are asked, also when the public limit of their address is spent.
    [Fact]
    public async Task TheHealthRoutesAreNotCountedByThePublicLimit()
    {
        var settings = Settings(trustedProxies: "127.0.0.1");
        settings["RateLimiting:Public:PermitLimit"] = "2";
        await using var application = await GameServiceHostSettings.StartAsync(settings);
        using var client = CreateClientWithoutRedirects(application);

        Assert.Equal(HttpStatusCode.Redirect, await RootAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.Redirect, await RootAsync(client, FirstClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RootAsync(client, FirstClient));

        for (var request = 0; request < 12; request++)
        {
            Assert.Equal(HttpStatusCode.OK, await LiveAsync(client, FirstClient));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await ReadyAsync(client, FirstClient));
        }
    }

    [Fact]
    public async Task TheHostTakesKestrelsLimitsAndTheShutdownTimeoutFromTheHttpSection()
    {
        var settings = Settings(trustedProxies: null);
        settings["Http:MaxRequestHeaderCount"] = "40";
        settings["Http:ShutdownTimeoutSeconds"] = "45";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(settings);
        var configuration = GameServiceConfiguration.Load(builder.Configuration, builder.Environment);

        builder.ConfigureGameServiceHost(configuration);
        await using var application = builder.Build();

        var limits = application.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        Assert.Equal(40, limits.MaxRequestHeaderCount);
        Assert.Equal(16 * 1024, limits.MaxRequestHeadersTotalSize);
        Assert.Equal(4 * 1024, limits.MaxRequestLineSize);
        Assert.Equal(TimeSpan.FromSeconds(15), limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(130), limits.KeepAliveTimeout);
        Assert.Equal(262_144, limits.MaxRequestBodySize);
        Assert.Null(limits.MaxConcurrentConnections);
        Assert.Equal(
            TimeSpan.FromSeconds(45),
            application.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    private static Dictionary<string, string?> Settings(string? trustedProxies)
    {
        var settings = GameServiceHostSettings.Required();
        settings["Http:TrustedProxies"] = trustedProxies;
        return settings;
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/login")
        {
            // Empty credentials are refused before PostgreSQL, after the limit counted them.
            Content = JsonContent.Create(new { login = "", password = "" })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> LiveAsync(HttpClient client, string forwardedFor)
    {
        return await GetAsync(client, "/health/live", forwardedFor);
    }

    // Readiness answers 200 or 503 with the state of its dependencies, never 429.
    private static async Task<HttpStatusCode> ReadyAsync(HttpClient client, string forwardedFor)
    {
        return await GetAsync(client, "/health/ready", forwardedFor);
    }

    private static async Task<HttpStatusCode> RootAsync(HttpClient client, string forwardedFor)
    {
        return await GetAsync(client, "/", forwardedFor);
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string path, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static HttpClient CreateClientWithoutRedirects(WebApplication application)
    {
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
    }

    private static async Task<(HttpStatusCode Status, string? Code)> BotTicketAsync(HttpClient client, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/development/simulation-bots/join-tickets")
        {
            Content = JsonContent.Create(new { botInstanceId = Guid.NewGuid(), botIndex = 0, shardId = (string?)null })
        };
        request.Headers.Add("X-Active-Simulation-Bots-Key", "not-the-secret");
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (response.StatusCode, problem.RootElement.GetProperty("code").GetString());
    }
}
