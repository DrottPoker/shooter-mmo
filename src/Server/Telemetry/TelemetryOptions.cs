using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Mmorpg.Server.Telemetry;

// Export settings shared by the server processes. Nothing leaves the process until
// Telemetry:OtlpEndpoint names an OpenTelemetry Collector; the meters still feed the
// periodic status logs either way.
public sealed record TelemetryOptions(
    Uri? OtlpEndpoint,
    TimeSpan MetricExportInterval,
    double TraceSampleRatio,
    string? ServiceInstanceId)
{
    public const string SectionName = "Telemetry";

    public static TelemetryOptions Disabled { get; } = new(null, TimeSpan.FromSeconds(10), 1d, null);

    public bool IsEnabled => OtlpEndpoint is not null;

    public static TelemetryOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var errors = new List<string>();

        var endpoint = ParseEndpoint(section["OtlpEndpoint"], errors);
        var exportIntervalSeconds = ParseInt(
            section["MetricExportIntervalSeconds"],
            "MetricExportIntervalSeconds",
            defaultValue: 10,
            minimum: 1,
            maximum: 300,
            errors);
        var traceSampleRatio = ParseRatio(section["TraceSampleRatio"], errors);
        var serviceInstanceId = ParseInstanceId(section["ServiceInstanceId"], errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return new TelemetryOptions(
            endpoint,
            TimeSpan.FromSeconds(exportIntervalSeconds),
            traceSampleRatio,
            serviceInstanceId);
    }

    // Overrides the process's default instance id, for example to tell two replicas on
    // one host apart. It becomes the Prometheus instance label.
    private static string? ParseInstanceId(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 128
            || !trimmed.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
        {
            errors.Add(
                $"{SectionName}:ServiceInstanceId must contain 1 to 128 ASCII letters, digits, hyphens, underscores, periods, or colons.");
            return null;
        }

        return trimmed;
    }

    // The OTLP gRPC exporter takes only scheme, host, and port.
    private static Uri? ParseEndpoint(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            errors.Add(
                $"{SectionName}:OtlpEndpoint must be an http or https URI with only a host and port, such as http://127.0.0.1:4317.");
            return null;
        }

        return endpoint;
    }

    private static int ParseInt(
        string? value,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            errors.Add($"{SectionName}:{key} must be an integer from {minimum} to {maximum}.");
            return defaultValue;
        }

        return parsed;
    }

    private static double ParseRatio(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1d;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed)
            || parsed < 0d
            || parsed > 1d)
        {
            errors.Add($"{SectionName}:TraceSampleRatio must be a number from 0 to 1.");
            return 1d;
        }

        return parsed;
    }
}
