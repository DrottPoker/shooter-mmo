using Mmorpg.GameService.Config;
using Mmorpg.Server.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Mmorpg.GameService;

// GameService telemetry: the meters every module declares, ASP.NET Core requests, and
// Npgsql commands. Health probes are not traced, because they would bury the requests
// that matter.
internal static class GameServiceTelemetry
{
    public const string ServiceName = "game-service";

    // Npgsql publishes its meter and activity source under this name.
    public const string NpgsqlInstrumentationName = "Npgsql";

    public static IReadOnlyList<string> MeterNames(GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return
        [
            .. GameServiceComposition.CreateModules(configuration)
                .SelectMany(module => module.MeterNames)
                .Distinct(StringComparer.Ordinal),
            NpgsqlInstrumentationName
        ];
    }

    public static WebApplicationBuilder AddGameServiceTelemetry(
        this WebApplicationBuilder builder,
        GameServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var resource = new TelemetryResource(
            ServiceName,
            TelemetryResource.ResolveServiceVersion(typeof(GameServiceTelemetry).Assembly),
            ResolveServiceInstanceId(configuration.Telemetry),
            builder.Environment.EnvironmentName,
            []);
        builder.AddServerTelemetry(
            configuration.Telemetry,
            resource,
            MeterNames(configuration),
            [NpgsqlInstrumentationName],
            metrics => metrics.AddAspNetCoreInstrumentation(),
            tracing => tracing.AddAspNetCoreInstrumentation(options =>
            {
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                options.RecordException = true;
            }));
        return builder;
    }

    // The host name keeps one series per host across restarts; replicas that share a
    // host must set Telemetry:ServiceInstanceId to stay apart.
    public static string ResolveServiceInstanceId(TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ServiceInstanceId ?? Environment.MachineName.ToLowerInvariant();
    }
}
