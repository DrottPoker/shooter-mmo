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

internal sealed class SimulationItemApiTestHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private SimulationItemApiTestHost(WebApplication application, HttpClient client)
    {
        this.application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    // Development by default; the environment decides how a route answers a malformed body. Logs,
    // when given, receive every entry from Debug up.
    public static async Task<SimulationItemApiTestHost> StartAsync(
        PostgresIntegrationTestContext context,
        string workerId,
        string workerSecret,
        string? environmentName = null,
        ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName ?? Environments.Development
        });
        builder.Configuration.AddConfiguration(context.Configuration);
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
        }

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
        application.MapSimulationItemEndpoints();
        await application.StartAsync();

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? throw new InvalidOperationException(
                "The simulation item API test server has no address feature.");
        return new SimulationItemApiTestHost(
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
