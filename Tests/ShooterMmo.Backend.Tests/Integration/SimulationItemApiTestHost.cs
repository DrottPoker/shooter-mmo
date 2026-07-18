using System.Net;
using AuthService.Auth;
using AuthService.Http;
using AuthService.Items;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShooterMmo.Backend.Tests.Integration;

internal sealed class SimulationItemApiTestHost : IAsyncDisposable
{
    private readonly WebApplication application;

    private SimulationItemApiTestHost(WebApplication application, HttpClient client)
    {
        this.application = application;
        Client = client;
    }

    public HttpClient Client { get; }

    public static async Task<SimulationItemApiTestHost> StartAsync(
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
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ServiceAuthentication:SimulationWorkers:{workerId}"] = workerSecret
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(context.DataSource);
        builder.Services.AddScoped<ItemTransactionService>();
        builder.Services.AddScoped<SimulationItemMutationService>();
        builder.Services.AddApiProblemDetails();
        builder.Services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SimulationWorkerAuthenticationHandler>(
                AuthenticationConstants.SimulationWorkerScheme,
                _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthenticationConstants.SimulationWorkerPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationConstants.SimulationWorkerScheme);
                policy.RequireAuthenticatedUser();
            });
        });

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
