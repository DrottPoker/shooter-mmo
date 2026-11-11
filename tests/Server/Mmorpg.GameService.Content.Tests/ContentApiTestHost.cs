using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Content.Tests;

// The Content module alone behind the GameService API pipeline, on a loopback port. A
// fallback policy requires a signed-in user, so a route that is not anonymous fails here.
// The revision check needs PostgreSQL and is tested on its own.
internal sealed class ContentApiTestHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private ContentApiTestHost(WebApplication application, HttpClient client)
    {
        this.application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ContentApiTestHost> StartAsync(ContentModuleOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization(authorization =>
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        var module = new ContentModule(options);
        module.AddServices(builder.Services);
        var revisionCheck = builder.Services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ContentRevisionCheck));
        builder.Services.Remove(revisionCheck);

        var application = builder.Build();
        application.UseApiPipeline();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        module.MapEndpoints(application);
        await application.StartAsync();

        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            .Single()
            ?? throw new InvalidOperationException("The content API test server has no address feature.");
        return new ContentApiTestHost(application, new HttpClient { BaseAddress = new Uri(address) });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
