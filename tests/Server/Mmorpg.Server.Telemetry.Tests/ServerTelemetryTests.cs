using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Mmorpg.Server.Telemetry.Tests;

public sealed class ServerTelemetryTests
{
    [Fact]
    public void ProjectMillisecondHistogramsResolveTheTickBudget()
    {
        var exported = new List<Metric>();
        using var projectMeter = new Meter("Mmorpg.Tests." + Guid.NewGuid().ToString("N"));
        using var foreignMeter = new Meter("Foreign.Tests." + Guid.NewGuid().ToString("N"));
        var projectHistogram = projectMeter.CreateHistogram<double>("project.duration", "ms");
        var foreignHistogram = foreignMeter.CreateHistogram<double>("foreign.duration", "ms");
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddServerMetrics([projectMeter.Name, foreignMeter.Name])
            .AddInMemoryExporter(exported)
            .Build();

        projectHistogram.Record(20);
        foreignHistogram.Record(20);
        provider.ForceFlush();

        Assert.Equal(ServerTelemetry.MillisecondHistogramBoundaries, Boundaries(exported, "project.duration"));
        Assert.Contains(33.34, Boundaries(exported, "project.duration"));
        Assert.DoesNotContain(33.34, Boundaries(exported, "foreign.duration"));
    }

    [Fact]
    public void DisabledTelemetryRegistersNoProvider()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.AddServerTelemetry(TelemetryOptions.Disabled, Resource(), ["Mmorpg.Tests"], ["Mmorpg.Tests"]);

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
    }

    [Fact]
    public void EnabledTelemetryRegistersMetricsTracesAndLogs()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var options = TelemetryOptions.Disabled with { OtlpEndpoint = new Uri("http://127.0.0.1:4317") };

        builder.AddServerTelemetry(options, Resource(), ["Mmorpg.Tests"], ["Mmorpg.Tests"]);

        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(MeterProvider));
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
        Assert.Contains(builder.Services, descriptor =>
            descriptor.ImplementationType?.FullName == "OpenTelemetry.Logs.OpenTelemetryLoggerProvider"
            || descriptor.ServiceType.FullName == "OpenTelemetry.Logs.LoggerProvider");
    }

    [Fact]
    public void ResourceIdentifiesTheProcess()
    {
        var attributes = ResourceBuilder.CreateEmpty()
            .AddServerResource(Resource())
            .Build()
            .Attributes
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Equal("test-service", attributes["service.name"]);
        Assert.Equal(TelemetryResource.ServiceNamespace, attributes["service.namespace"]);
        Assert.Equal("1.2.3", attributes["service.version"]);
        Assert.Equal("instance-1", attributes["service.instance.id"]);
        Assert.Equal("Testing", attributes[ServerTelemetry.DeploymentEnvironmentAttribute]);
        Assert.Equal("shard-1", attributes["mmorpg.shard.id"]);
    }

    [Fact]
    public void SamplingFollowsTheCaller()
    {
        var sampler = ServerTelemetry.CreateSampler(0d);
        var sampledCaller = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            isRemote: true);

        var root = sampler.ShouldSample(new SamplingParameters(
            default,
            ActivityTraceId.CreateRandom(),
            "root",
            ActivityKind.Internal));
        var child = sampler.ShouldSample(new SamplingParameters(
            sampledCaller,
            sampledCaller.TraceId,
            "child",
            ActivityKind.Server));

        Assert.Equal(SamplingDecision.Drop, root.Decision);
        Assert.Equal(SamplingDecision.RecordAndSample, child.Decision);
    }

    private static TelemetryResource Resource()
    {
        return new TelemetryResource(
            "test-service",
            "1.2.3",
            "instance-1",
            "Testing",
            [new KeyValuePair<string, object>("mmorpg.shard.id", "shard-1")]);
    }

    private static double[] Boundaries(List<Metric> metrics, string name)
    {
        var metric = metrics.Last(candidate => candidate.Name == name);
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            var bounds = new List<double>();
            foreach (var bucket in point.GetHistogramBuckets())
            {
                if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                {
                    bounds.Add(bucket.ExplicitBound);
                }
            }

            return [.. bounds];
        }

        throw new InvalidOperationException($"{name} exported no metric point.");
    }
}
