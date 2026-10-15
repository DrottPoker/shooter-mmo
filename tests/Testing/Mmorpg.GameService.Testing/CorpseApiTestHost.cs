using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Testing;

internal sealed class CorpseApiTestHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private CorpseApiTestHost(WebApplication application, HttpClient client)
    {
        this.application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<CorpseApiTestHost> StartAsync(
        PostgresIntegrationTestContext context,
        string workerId,
        string workerSecret)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddConfiguration(context.Configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var configuration = context.GameServiceConfiguration with
        {
            Accounts = context.GameServiceConfiguration.Accounts with
            {
                SimulationWorkerCredentials = new SimulationWorkerCredentials(
                    [new KeyValuePair<string, string?>(workerId, workerSecret)],
                    null)
            }
        };
        builder.Services.AddGameServiceForTests(context.DataSource, configuration);

        var application = builder.Build();
        application.UseApiPipeline();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCorpseEndpoints();
        await application.StartAsync();

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? throw new InvalidOperationException("The corpse API test server has no address feature.");
        return new CorpseApiTestHost(
            application,
            new HttpClient
            {
                BaseAddress = new Uri(addresses.Single())
            });
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
