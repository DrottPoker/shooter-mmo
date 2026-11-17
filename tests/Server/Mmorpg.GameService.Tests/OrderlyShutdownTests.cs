using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameService.Platform.Hosting;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests;

// At the stop signal readiness turns to "stopping" at once, GameService keeps serving through
// the drain, and a request in flight still completes before the host stops.
public sealed class OrderlyShutdownTests
{
    [PostgresIntegrationFact]
    public async Task ReadinessTurnsUnreadyAtTheStopSignalWhileARequestInFlightCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await using var redis = FakeRedisServer.Start();
        var configuration = context.GameServiceConfiguration with
        {
            Platform = context.GameServiceConfiguration.Platform with
            {
                RedisConnectionString = redis.ConnectionString(),
                Http = HttpServerOptions.Default with
                {
                    ShutdownDrain = TimeSpan.FromSeconds(3),
                    ShutdownTimeout = TimeSpan.FromSeconds(30)
                }
            }
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.ConfigureGameServiceHost(configuration);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddGameServiceForTests(context.DataSource, configuration);
        // The kit removes every GameService background service; this test needs the drain.
        builder.Services.AddHostedService<ShutdownDrainService>();
        await using var application = builder.Build();
        application.UseGameServicePipeline();
        application.MapGameService(configuration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        application.MapGet("/test/in-flight", async () =>
        {
            entered.SetResult();
            await release.Task;
            return Results.Text("finished");
        });
        await application.StartAsync(cancellationToken);
        using var client = GameServiceHostSettings.CreateClient(application);

        Assert.Equal((HttpStatusCode.OK, "ready"), await ReadinessAsync(client));
        var inFlight = client.GetAsync("/test/in-flight", cancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        // The signal raises ApplicationStopping before the host stops anything.
        application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "stopping"), await ReadinessAsync(client));
        var stopped = application.StopAsync(CancellationToken.None);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "stopping"), await ReadinessAsync(client));
        Assert.False(stopped.IsCompleted);

        release.SetResult();
        using (var response = await inFlight)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("finished", await response.Content.ReadAsStringAsync(cancellationToken));
        }

        await stopped.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static async Task<(HttpStatusCode Status, string? Readiness)> ReadinessAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (response.StatusCode, health.RootElement.GetProperty("status").GetString());
    }
}
