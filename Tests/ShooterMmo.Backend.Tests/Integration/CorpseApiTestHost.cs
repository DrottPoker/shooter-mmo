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

namespace ShooterMmo.Backend.Tests.Integration;

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
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ServiceAuthentication:SimulationWorkers:{workerId}"] = workerSecret
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(context.DataSource);
        builder.Services.AddSingleton(context.AuthServiceConfig);
        builder.Services.AddScoped<ItemTransactionService>();
        builder.Services.AddScoped<CorpseService>();
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
