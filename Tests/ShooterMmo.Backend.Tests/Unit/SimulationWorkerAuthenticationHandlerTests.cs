using AuthService.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class SimulationWorkerAuthenticationHandlerTests
{
    private const string SharedSecret =
        "test-shared-simulation-worker-secret-at-least-32-characters";

    [Fact]
    public async Task SharedCredentialModeDoesNotHardcodeAWorkerIdentity()
    {
        var result = await AuthenticateAsync(
            new Dictionary<string, string?>
            {
                ["SIMULATION_WORKER_SERVICE_SECRET"] = SharedSecret
            },
            "configured-worker",
            SharedSecret);

        Assert.True(result.Succeeded);
        Assert.Equal("configured-worker", result.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task PerWorkerCredentialModeDoesNotFallBackForUnknownWorker()
    {
        var result = await AuthenticateAsync(
            new Dictionary<string, string?>
            {
                ["SIMULATION_WORKER_SERVICE_SECRET"] = SharedSecret,
                ["ServiceAuthentication:SimulationWorkers:allowed-worker"] =
                    "test-allowed-simulation-worker-secret-at-least-32-characters"
            },
            "unknown-worker",
            SharedSecret);

        Assert.False(result.Succeeded);
        Assert.Equal("Simulation worker credentials are invalid.", result.Failure!.Message);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        IReadOnlyDictionary<string, string?> settings,
        string workerId,
        string workerSecret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SimulationWorkerAuthenticationHandler>(
                AuthenticationConstants.SimulationWorkerScheme,
                _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };
        context.Request.Headers[AuthenticationConstants.SimulationWorkerIdHeader] = workerId;
        context.Request.Headers[AuthenticationConstants.SimulationWorkerSecretHeader] = workerSecret;

        return await context.AuthenticateAsync(AuthenticationConstants.SimulationWorkerScheme);
    }
}
