using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Http;
using Mmorpg.GameService.Items;

namespace Mmorpg.Backend.Tests.Integration;

internal sealed class ItemApiTestHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private ItemApiTestHost(WebApplication application, HttpClient client)
    {
        this.application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<ItemApiTestHost> StartAsync(PostgresIntegrationTestContext context)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddConfiguration(context.Configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddGameServiceForTests(context.DataSource, context.GameServiceConfiguration);

        var application = builder.Build();
        application.UseApiPipeline();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapItemEndpoints();
        await application.StartAsync();

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? throw new InvalidOperationException("The item API test server has no address feature.");
        var address = addresses.Single();
        return new ItemApiTestHost(
            application,
            new HttpClient
            {
                BaseAddress = new Uri(address)
            });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
