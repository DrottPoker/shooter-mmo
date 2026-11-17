using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Platform.Tests;

// Only a trusted proxy may name the client. The test client connects from 127.0.0.1, so a
// configuration that trusts 127.0.0.1 makes it a proxy on the same host.
public sealed class ClientAddressTests
{
    private const string ForwardedClient = "203.0.113.7";

    [Fact]
    public async Task ATrustedProxyNamesTheClientAndItsScheme()
    {
        var logs = new ScopeCapturingLoggerProvider();
        await using var application = await StartAsync([IPAddress.Loopback], [], logs);
        using var client = CreateClient(application);

        var seen = await SendAsync(client, ForwardedClient, "https");

        Assert.Equal(ForwardedClient, seen.Address);
        Assert.Equal("https", seen.Scheme);
        Assert.False(seen.IsLoopback);
        var entry = Assert.Single(logs.Entries, entry => entry.Message == "Inside the request.");
        Assert.Contains(new KeyValuePair<string, object?>("ClientAddress", ForwardedClient), entry.ScopeValues);
    }

    [Fact]
    public async Task ATrustedNetworkNamesTheClient()
    {
        await using var application = await StartAsync([], [IPNetwork.Parse("127.0.0.0/8")]);
        using var client = CreateClient(application);

        var seen = await SendAsync(client, ForwardedClient, null);

        Assert.Equal(ForwardedClient, seen.Address);
    }

    // A caller that is not a trusted proxy keeps its own address and is served normally.
    [Fact]
    public async Task AnUntrustedCallerKeepsItsOwnAddress()
    {
        await using var application = await StartAsync([IPAddress.Parse("10.0.0.1")], []);
        using var client = CreateClient(application);

        var seen = await SendAsync(client, ForwardedClient, "https");

        Assert.Equal("127.0.0.1", seen.Address);
        Assert.Equal("http", seen.Scheme);
        Assert.True(seen.IsLoopback);
    }

    [Fact]
    public async Task WithoutTrustedProxiesForwardedHeadersAreIgnored()
    {
        await using var application = await StartAsync([], []);
        using var client = CreateClient(application);

        var seen = await SendAsync(client, ForwardedClient, "https");

        Assert.Equal("127.0.0.1", seen.Address);
        Assert.True(seen.IsLoopback);
    }

    // The proxy appends the address it saw, so with one proxy only the last entry is trusted:
    // whatever the client wrote before it is ignored.
    [Fact]
    public async Task OnlyTheEntriesOfTheForwardLimitCount()
    {
        await using var application = await StartAsync([IPAddress.Loopback], []);
        using var client = CreateClient(application);

        var seen = await SendAsync(client, "127.0.0.1, " + ForwardedClient, null);

        Assert.Equal(ForwardedClient, seen.Address);
        Assert.False(seen.IsLoopback);
    }

    [Theory]
    [InlineData("198.51.100.4", "198.51.100.4")]
    [InlineData("::ffff:198.51.100.4", "198.51.100.4")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("::1", "::1")]
    public void AddressesPartitionPerIpv4AddressAndPerIpv6Network(string address, string partition)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        Assert.Equal(partition, ClientAddress.PartitionKey(context));
    }

    [Fact]
    public void AnUnknownAddressIsItsOwnPartitionAndNotLoopback()
    {
        var context = new DefaultHttpContext();

        Assert.Equal(ClientAddress.UnknownPartition, ClientAddress.PartitionKey(context));
        Assert.False(ClientAddress.IsLoopback(context));
    }

    private static async Task<SeenRequest> SendAsync(HttpClient client, string forwardedFor, string? forwardedProto)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SeenRequest>(TestContext.Current.CancellationToken))!;
    }

    private static async Task<WebApplication> StartAsync(
        IReadOnlyList<IPAddress> proxies,
        IReadOnlyList<IPNetwork> networks,
        ScopeCapturingLoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();
        builder.Services.AddSingleton(new PlatformOptions(
            "Host=127.0.0.1;Database=test;Username=test;Password=test",
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1),
            false)
        {
            Http = HttpServerOptions.Default with { TrustedProxies = proxies, TrustedNetworks = networks }
        });

        var application = builder.Build();
        application.UseForwardedClientAddresses();
        application.UseApiPipeline();
        application.MapGet("/who", (HttpContext context, ILogger<ClientAddressTests> logger) =>
        {
            logger.LogInformation("Inside the request.");
            return Results.Ok(new SeenRequest(
                ClientAddress.Of(context)?.ToString(),
                context.Request.Scheme,
                ClientAddress.IsLoopback(context)));
        });
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

    private sealed record SeenRequest(string? Address, string Scheme, bool IsLoopback);
}
