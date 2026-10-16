using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mmorpg.Server.Telemetry;

// Wires metrics, traces, and logs of one server process to an OpenTelemetry Collector
// over OTLP. The project's meters record through lock-free counters and histograms, and
// the SDK reads observable instruments on its own export thread, so enabling export adds
// no work to the realtime loop.
public static class ServerTelemetry
{
    public const string ProjectInstrumentationPrefix = "Mmorpg.";
    public const string RuntimeMeterName = "System.Runtime";
    public const string DeploymentEnvironmentAttribute = "deployment.environment.name";

    // Bucket bounds for the project's millisecond histograms. They resolve the 33.3 ms
    // fixed-tick budget below one millisecond and still cover item transactions up to
    // their 10 second timeout.
    public static IReadOnlyList<double> MillisecondHistogramBoundaries { get; } =
    [
        0.1, 0.25, 0.5, 1, 2, 4, 8, 16, 24, 33.34, 50, 100, 250, 500, 1000, 2500, 5000, 10000
    ];

    public static IHostApplicationBuilder AddServerTelemetry(
        this IHostApplicationBuilder builder,
        TelemetryOptions options,
        TelemetryResource resource,
        IEnumerable<string> meterNames,
        IEnumerable<string> activitySourceNames,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(meterNames);
        ArgumentNullException.ThrowIfNull(activitySourceNames);

        if (!options.IsEnabled)
        {
            return builder;
        }

        var endpoint = options.OtlpEndpoint!;
        var meters = meterNames.ToArray();
        var sources = activitySourceNames.ToArray();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder.AddServerResource(resource))
            .WithMetrics(metrics =>
            {
                metrics.AddServerMetrics(meters);
                configureMetrics?.Invoke(metrics);
                metrics.AddOtlpExporter((exporterOptions, readerOptions) =>
                {
                    ConfigureExporter(exporterOptions, endpoint);
                    readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                    readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                        (int)options.MetricExportInterval.TotalMilliseconds;
                });
            })
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(CreateSampler(options.TraceSampleRatio))
                    .AddSource(sources)
                    .AddHttpClientInstrumentation(instrumentation =>
                        instrumentation.RecordException = true);
                configureTracing?.Invoke(tracing);
                tracing.AddOtlpExporter(exporterOptions => ConfigureExporter(exporterOptions, endpoint));
            })
            .WithLogging(
                logging => logging.AddOtlpExporter(exporterOptions =>
                    ConfigureExporter(exporterOptions, endpoint)),
                loggerOptions =>
                {
                    // Scopes carry the correlation id, and the formatted message keeps
                    // log lines readable in Grafana next to their structured attributes.
                    loggerOptions.IncludeScopes = true;
                    loggerOptions.IncludeFormattedMessage = true;
                });
        return builder;
    }

    // The meters and histogram views of a server process. Production and the allocation
    // tests share it, so the tests measure the configuration that runs.
    public static MeterProviderBuilder AddServerMetrics(
        this MeterProviderBuilder metrics,
        IEnumerable<string> meterNames)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(meterNames);

        return metrics
            .AddMeter([.. meterNames])
            .AddMeter(RuntimeMeterName)
            .AddHttpClientInstrumentation()
            .AddView(SelectHistogramConfiguration);
    }

    public static ResourceBuilder AddServerResource(
        this ResourceBuilder builder,
        TelemetryResource resource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        return builder
            .AddService(
                resource.ServiceName,
                TelemetryResource.ServiceNamespace,
                resource.ServiceVersion,
                autoGenerateServiceInstanceId: false,
                serviceInstanceId: resource.ServiceInstanceId)
            .AddAttributes(
            [
                new KeyValuePair<string, object>(
                    DeploymentEnvironmentAttribute,
                    resource.DeploymentEnvironment),
                .. resource.Attributes
            ]);
    }

    internal static MetricStreamConfiguration? SelectHistogramConfiguration(Instrument instrument)
    {
        return instrument is Histogram<double>
            && string.Equals(instrument.Unit, "ms", StringComparison.Ordinal)
            && instrument.Meter.Name.StartsWith(ProjectInstrumentationPrefix, StringComparison.Ordinal)
                ? new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [.. MillisecondHistogramBoundaries]
                }
                : null;
    }

    // A sampled caller keeps its whole trace, so a worker operation and the GameService
    // request and database commands it causes are sampled together.
    internal static Sampler CreateSampler(double traceSampleRatio)
    {
        return new ParentBasedSampler(new TraceIdRatioBasedSampler(traceSampleRatio));
    }

    private static void ConfigureExporter(OtlpExporterOptions exporterOptions, Uri endpoint)
    {
        exporterOptions.Endpoint = endpoint;
        exporterOptions.Protocol = OtlpExportProtocol.Grpc;
    }
}
