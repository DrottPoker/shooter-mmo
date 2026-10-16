using System.Reflection;

namespace Mmorpg.Server.Telemetry;

// The OpenTelemetry resource that identifies one server process. Prometheus promotes
// the attributes listed in observability/prometheus/prometheus.yml to labels, so every
// attribute must have one stable value per process.
public sealed record TelemetryResource(
    string ServiceName,
    string ServiceVersion,
    string ServiceInstanceId,
    string DeploymentEnvironment,
    IReadOnlyList<KeyValuePair<string, object>> Attributes)
{
    public const string ServiceNamespace = "mmorpg";

    public static string ResolveServiceVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString() ?? "unknown"
            : informationalVersion;
    }
}
