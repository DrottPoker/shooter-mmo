using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace Mmorpg.Testing;

// Reads the metric names that the Grafana dashboards, the Prometheus alert rules, and
// the rule tests in observability/ reference, and maps the project's instruments to the
// names Prometheus stores them under. A renamed or removed instrument then fails a test
// instead of leaving an empty panel or an alert that can never fire.
public static partial class ObservabilityAssets
{
    public static string Directory => Path.Combine(RepositoryPaths.Root, "observability");

    public static IReadOnlySet<string> ReferencedMetricNames(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var files = System.IO.Directory
            .EnumerateFiles(Path.Combine(Directory, "grafana", "dashboards"), "*.json")
            .Concat(System.IO.Directory.EnumerateFiles(Path.Combine(Directory, "prometheus"), "*.yml", SearchOption.AllDirectories));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match match in MetricNamePattern().Matches(File.ReadAllText(file)))
            {
                if (match.Value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    names.Add(match.Value);
                }
            }
        }

        return names;
    }

    // The OTLP translation of Prometheus 3 with its default UnderscoreEscapingWithSuffixes
    // strategy, limited to the units and instrument kinds this project uses.
    public static IReadOnlyList<string> PrometheusNames(Instrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        var name = instrument.Name.Replace('.', '_');
        name += instrument.Unit switch
        {
            null or "" => string.Empty,
            "ms" => "_milliseconds",
            _ => throw new NotSupportedException(
                $"Unit '{instrument.Unit}' of {instrument.Name} has no Prometheus suffix mapping in the test helper.")
        };
        var kind = instrument.GetType().Name;
        return kind switch
        {
            "Counter`1" or "ObservableCounter`1" => [name + "_total"],
            "Histogram`1" => [name + "_bucket", name + "_sum", name + "_count"],
            "ObservableGauge`1" or "Gauge`1" or "UpDownCounter`1" or "ObservableUpDownCounter`1" => [name],
            _ => throw new NotSupportedException($"Instrument kind {kind} has no Prometheus name mapping.")
        };
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b")]
    private static partial Regex MetricNamePattern();
}
