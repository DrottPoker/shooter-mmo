using Microsoft.Extensions.Configuration;

namespace Mmorpg.Server.Telemetry.Tests;

public sealed class TelemetryOptionsTests
{
    [Fact]
    public void ExportStaysOffWithoutAnEndpoint()
    {
        var options = TelemetryOptions.FromConfiguration(Configuration([]));

        Assert.False(options.IsEnabled);
        Assert.Equal(TelemetryOptions.Disabled, options);
    }

    [Fact]
    public void ReadsEveryExportSetting()
    {
        var options = TelemetryOptions.FromConfiguration(Configuration(new()
        {
            ["Telemetry:OtlpEndpoint"] = " http://127.0.0.1:4317 ",
            ["Telemetry:MetricExportIntervalSeconds"] = "15",
            ["Telemetry:TraceSampleRatio"] = "0.25",
            ["Telemetry:ServiceInstanceId"] = "game-service-a"
        }));

        Assert.True(options.IsEnabled);
        Assert.Equal(new Uri("http://127.0.0.1:4317"), options.OtlpEndpoint);
        Assert.Equal(TimeSpan.FromSeconds(15), options.MetricExportInterval);
        Assert.Equal(0.25, options.TraceSampleRatio);
        Assert.Equal("game-service-a", options.ServiceInstanceId);
    }

    [Fact]
    public void ReportsEveryInvalidSettingAtOnce()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TelemetryOptions.FromConfiguration(Configuration(new()
            {
                ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:4317/v1/metrics",
                ["Telemetry:MetricExportIntervalSeconds"] = "0",
                ["Telemetry:TraceSampleRatio"] = "1.5",
                ["Telemetry:ServiceInstanceId"] = "two words"
            })));

        Assert.Contains("Telemetry:OtlpEndpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Telemetry:MetricExportIntervalSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Telemetry:TraceSampleRatio", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Telemetry:ServiceInstanceId", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("collector:4317")]
    [InlineData("ftp://collector:4317")]
    [InlineData("http://user:secret@collector:4317")]
    [InlineData("http://collector:4317/?token=abc")]
    public void RejectsAnEndpointThatIsNotAPlainHostAndPort(string endpoint)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TelemetryOptions.FromConfiguration(Configuration(new()
            {
                ["Telemetry:OtlpEndpoint"] = endpoint
            })));

        Assert.Contains("Telemetry:OtlpEndpoint", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
