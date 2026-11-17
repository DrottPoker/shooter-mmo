using System.Net;

namespace Mmorpg.GameService.Platform.Configuration;

// The Http section: which proxies may name the client, Kestrel's request limits, and the
// orderly stop. AllowedHosts is ASP.NET Core's own key; GameService only checks it outside
// Development, as docs/SERVICE_FEATURES.md describes under Behind A Proxy.
public sealed record HttpServerOptions(
    IReadOnlyList<IPAddress> TrustedProxies,
    IReadOnlyList<IPNetwork> TrustedNetworks,
    int ForwardLimit,
    int MaxRequestHeadersTotalSizeBytes,
    int MaxRequestHeaderCount,
    int MaxRequestLineSizeBytes,
    TimeSpan RequestHeadersTimeout,
    TimeSpan KeepAliveTimeout,
    TimeSpan ShutdownDrain,
    TimeSpan ShutdownTimeout)
{
    public const string TrustedProxiesKey = "Http:TrustedProxies";
    public const string ForwardLimitKey = "Http:ForwardLimit";
    public const string MaxRequestHeadersTotalSizeKey = "Http:MaxRequestHeadersTotalSizeBytes";
    public const string MaxRequestHeaderCountKey = "Http:MaxRequestHeaderCount";
    public const string MaxRequestLineSizeKey = "Http:MaxRequestLineSizeBytes";
    public const string RequestHeadersTimeoutKey = "Http:RequestHeadersTimeoutSeconds";
    public const string KeepAliveTimeoutKey = "Http:KeepAliveTimeoutSeconds";
    public const string ShutdownDrainKey = "Http:ShutdownDrainSeconds";
    public const string ShutdownTimeoutKey = "Http:ShutdownTimeoutSeconds";
    public const string AllowedHostsKey = "AllowedHosts";

    public static HttpServerOptions Default { get; } = new(
        [],
        [],
        1,
        16 * 1024,
        64,
        4 * 1024,
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(130),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30));

    // The value of AllowedHosts as configured, checked outside Development.
    public string? AllowedHosts { get; init; }

    // Without a trusted proxy, forwarded headers are ignored and the connection names the client.
    public bool TrustsProxies => TrustedProxies.Count > 0 || TrustedNetworks.Count > 0;

    public static HttpServerOptions Read(IConfiguration configuration, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var (proxies, networks) = ReadTrustedProxies(configuration[TrustedProxiesKey], errors);
        var drainSeconds = ConfigurationValidation.ReadOptionalIntRange(
            configuration,
            ShutdownDrainKey,
            (int)Default.ShutdownDrain.TotalSeconds,
            0,
            300,
            errors);
        var timeoutSeconds = ConfigurationValidation.ReadOptionalIntRange(
            configuration,
            ShutdownTimeoutKey,
            (int)Default.ShutdownTimeout.TotalSeconds,
            1,
            3_600,
            errors);
        if (timeoutSeconds <= drainSeconds)
        {
            errors.Add(
                $"{ShutdownTimeoutKey} must be longer than {ShutdownDrainKey}, so requests in flight have time to finish after the drain.");
        }

        return new HttpServerOptions(
            proxies,
            networks,
            ConfigurationValidation.ReadOptionalIntRange(
                configuration, ForwardLimitKey, Default.ForwardLimit, 1, 10, errors),
            ConfigurationValidation.ReadOptionalIntRange(
                configuration, MaxRequestHeadersTotalSizeKey, Default.MaxRequestHeadersTotalSizeBytes, 4_096, 1_048_576, errors),
            ConfigurationValidation.ReadOptionalIntRange(
                configuration, MaxRequestHeaderCountKey, Default.MaxRequestHeaderCount, 16, 1_000, errors),
            ConfigurationValidation.ReadOptionalIntRange(
                configuration, MaxRequestLineSizeKey, Default.MaxRequestLineSizeBytes, 1_024, 65_536, errors),
            TimeSpan.FromSeconds(ConfigurationValidation.ReadOptionalIntRange(
                configuration, RequestHeadersTimeoutKey, (int)Default.RequestHeadersTimeout.TotalSeconds, 1, 300, errors)),
            TimeSpan.FromSeconds(ConfigurationValidation.ReadOptionalIntRange(
                configuration, KeepAliveTimeoutKey, (int)Default.KeepAliveTimeout.TotalSeconds, 1, 3_600, errors)),
            TimeSpan.FromSeconds(drainSeconds),
            TimeSpan.FromSeconds(timeoutSeconds))
        {
            AllowedHosts = configuration[AllowedHostsKey]
        };
    }

    // The entries that ASP.NET Core's host filtering reads as "any host": one of them among the
    // allowed hosts switches the filtering off, whatever else the list names.
    private static readonly string[] AnyHostEntries = ["*", "0.0.0.0", "[::]"];

    // Outside Development, Host filtering must name real host names: an entry for any host, or no
    // value, which ASP.NET Core also reads as any host, would answer a request for any host.
    public IReadOnlyList<string> ErrorsOutsideDevelopment()
    {
        var hosts = (AllowedHosts ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(host => AnyHostEntries.Contains(host, StringComparer.Ordinal)))
        {
            return
            [
                $"{AllowedHostsKey} must name the host names of GameService, separated by ';', such as 'api.example.com;game-service'; '*', '0.0.0.0', and '[::]' allow any host and are allowed only in the Development environment."
            ];
        }

        return [];
    }

    // A list of addresses and CIDR networks separated by ';' or ','.
    private static (IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks) ReadTrustedProxies(
        string? value,
        ICollection<string> errors)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (var entry in (value ?? string.Empty).Split(
                     [';', ','],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                // A network names its first address, so 10.0.0.1/8 is refused as ambiguous.
                if (IPNetwork.TryParse(entry, out var network)
                    && IPAddress.TryParse(entry[..entry.IndexOf('/', StringComparison.Ordinal)], out var baseAddress)
                    && network.BaseAddress.Equals(baseAddress))
                {
                    networks.Add(network);
                    continue;
                }
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
                continue;
            }

            errors.Add(
                $"{TrustedProxiesKey} entry '{entry}' must be an IP address or a network such as 10.0.0.0/8.");
        }

        return (proxies, networks);
    }
}
