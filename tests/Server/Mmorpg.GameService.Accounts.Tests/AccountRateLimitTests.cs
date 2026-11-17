using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Accounts.Tests;

// The account limits as the Accounts module registers them, behind the GameService order of
// authentication, authorization, and then the limiter. A test scheme signs the caller in as the
// account its header names; every caller shares the loopback address, as 500 load bots do.
public sealed class AccountRateLimitTests
{
    private const string AccountHeader = "X-Test-Account";
    private static readonly Guid FirstAccount = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondAccount = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task EachActionRouteCountsPerAccountRejectsAlikeAndResets()
    {
        await using var application = await StartAsync(actions: new FixedWindowRateLimit(2, TimeSpan.FromSeconds(1)));
        using var client = CreateClient(application);

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/join", FirstAccount));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/join", FirstAccount));
        await AssertRejectedAsync(client, HttpMethod.Post, "/join", FirstAccount);

        // Another route of the same account and the same route of another account have their own windows.
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/logout", FirstAccount));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/join", SecondAccount));

        await Task.Delay(TimeSpan.FromMilliseconds(1_200), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/join", FirstAccount));
    }

    [Fact]
    public async Task ReadRoutesShareOneWindowPerAccountAndReset()
    {
        await using var application = await StartAsync(reads: new FixedWindowRateLimit(2, TimeSpan.FromSeconds(1)));
        using var client = CreateClient(application);

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/characters", FirstAccount));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/item-state", FirstAccount));
        await AssertRejectedAsync(client, HttpMethod.Get, "/characters", FirstAccount);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/characters", SecondAccount));

        await Task.Delay(TimeSpan.FromMilliseconds(1_200), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Get, "/item-state", FirstAccount));
    }

    // Authorization runs before the limiter, so a caller without a session is refused as before
    // and does not use up an account's window.
    [Fact]
    public async Task AnUnauthenticatedRequestIsRefusedBeforeTheLimit()
    {
        await using var application = await StartAsync(actions: new FixedWindowRateLimit(1, TimeSpan.FromMinutes(1)));
        using var client = CreateClient(application);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Post, "/join", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Post, "/join", null));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/join", FirstAccount));
    }

    private static async Task AssertRejectedAsync(HttpClient client, HttpMethod method, string path, Guid account)
    {
        using var request = Request(method, path, account);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter.Delta.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("rate_limit_exceeded", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string path, Guid? account)
    {
        using var request = Request(method, path, account);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid? account)
    {
        var request = new HttpRequestMessage(method, path);
        if (account is { } id)
        {
            request.Headers.Add(AccountHeader, id.ToString());
        }

        return request;
    }

    private static async Task<WebApplication> StartAsync(
        FixedWindowRateLimit? actions = null,
        FixedWindowRateLimit? reads = null)
    {
        var options = new AccountsModuleOptions(
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            5,
            TimeSpan.FromSeconds(60),
            new SimulationWorkerCredentials([], null))
        {
            AccountActionsRateLimit = actions ?? AccountsModuleOptions.DefaultAccountActionsRateLimit,
            AccountReadsRateLimit = reads ?? AccountsModuleOptions.DefaultAccountReadsRateLimit
        };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();
        builder.Services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAccountHandler>(AuthenticationConstants.AccountSessionScheme, _ => { });
        builder.Services.AddAuthorization(authorization => authorization.AddPolicy(
            AuthenticationConstants.AccountSessionPolicy,
            policy => policy
                .AddAuthenticationSchemes(AuthenticationConstants.AccountSessionScheme)
                .RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(limiter => AccountsModule.AddRateLimitPolicies(limiter, options));

        var application = builder.Build();
        application.UseApiPipeline();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseRateLimiter();
        Map(application.MapPost("/join", () => Results.Ok()), AccountRateLimitPolicies.Action);
        Map(application.MapPost("/logout", () => Results.Ok()), AccountRateLimitPolicies.Action);
        Map(application.MapGet("/characters", () => Results.Ok()), AccountRateLimitPolicies.Read);
        Map(application.MapGet("/item-state", () => Results.Ok()), AccountRateLimitPolicies.Read);
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private static void Map(RouteHandlerBuilder route, string policy)
    {
        route.RequireAuthorization(AuthenticationConstants.AccountSessionPolicy).RequireRateLimiting(policy);
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

    private sealed class TestAccountHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var account = Request.Headers[AccountHeader].ToString();
            if (string.IsNullOrEmpty(account))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(AuthenticationConstants.AccountIdClaim, account)],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
