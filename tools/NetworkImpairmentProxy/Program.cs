using System.Globalization;
using System.Net;
using Mmorpg.Tools.NetworkImpairmentProxy;

internal static class Program
{
    public const string HelpText =
        """
        NetworkImpairmentProxy

        A UDP proxy between game clients and a SimulationWorker that delays, jitters, drops,
        duplicates, and cuts off packets by a network profile of tools/load/network-profiles.
        The worker advertises the proxy's port, so the client needs no change.

        NetworkImpairmentProxy --profile <name|file> --listen <ip:port> --forward <ip:port> [options]
          --profile <name|file>           A profile name, such as normal, or a profile file.
          --listen <ip:port>              Where clients send to, such as 127.0.0.1:27115.
          --forward <ip:port>             The worker's UDP endpoint, such as 127.0.0.1:27015.
          --seed <number>                 The seed of every random decision. Default: 1
          --counts <file>                 Writes the counts as JSON when the proxy stops.
          --idle-timeout-seconds <s>      Forgets a client that sent and got nothing this long. Default: 30
          --report-interval-seconds <s>   Prints what happened so far this often; 0 never. Default: 10
          --max-clients <count>           Refuses more clients than this at once. Default: 2048
          --allow-non-loopback            Allows a listen or forward address off this machine.

        NetworkImpairmentProxy self-test [--delay-ms <ms>] [--seconds <s>] [--clients <n>]
                                         [--rate <packets per second per client>] [--size <bytes>]
                                         [--spin-ms <ms>] [--output <file>]
          Sends timestamped packets through the proxy with a constant delay and reports the error
          of the applied delay. Defaults: 40 ms, 20 s, 8 clients, 100 packets per second, 120 bytes,
          and the proxy's spin of 0.3 ms before a due time.

        Exit codes: 0 the proxy stopped on Ctrl+C or the self-test passed, 1 it failed, 2 invalid
        options.
        """;

    public static int Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        try
        {
            return args.Length > 0 && args[0] == "self-test"
                ? RunSelfTest(ProxyArguments.Parse(args[1..], SelfTestKeys))
                : RunProxy(ProxyArguments.Parse(args, ProxyKeys));
        }
        catch (NetworkImpairmentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Use --help to list the options.");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The network impairment proxy failed: {exception.Message}");
            return 1;
        }
    }

    private static readonly string[] ProxyKeys =
    [
        "profile", "listen", "forward", "seed", "counts", "idle-timeout-seconds",
        "report-interval-seconds", "max-clients", "allow-non-loopback"
    ];

    private static readonly string[] SelfTestKeys = ["delay-ms", "seconds", "clients", "rate", "size", "output", "spin-ms"];

    private static int RunProxy(ProxyArguments arguments)
    {
        var workingDirectory = Directory.GetCurrentDirectory();
        var profile = NetworkProfile.Resolve(arguments.Require("profile"), workingDirectory);
        var options = new ImpairmentProxyOptions(
            ProxyArguments.ParseEndPoint(arguments.Require("listen"), "listen"),
            ProxyArguments.ParseEndPoint(arguments.Require("forward"), "forward"),
            arguments.Long("seed", 1),
            TimeSpan.FromSeconds(arguments.Int("idle-timeout-seconds", 30, 1, 86_400)),
            arguments.Int("max-clients", ImpairmentProxyOptions.DefaultMaximumClients, 1, 65_535),
            arguments.Flag("allow-non-loopback"));
        var reportInterval = arguments.Int("report-interval-seconds", 10, 0, 86_400);
        var countsPath = arguments.Optional("counts") is { } counts ? Path.GetFullPath(counts, workingDirectory) : null;

        using var stopped = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopped.Set();
        };

        var proxy = new ImpairmentProxy(options, profile);
        proxy.Start();
        Console.WriteLine(FormattableString.Invariant(
            $"Network impairment proxy listening on UDP {proxy.ListenEndPoint}, forwarding to {options.Forward} with profile {profile.Name} (seed {options.Seed}): {profile.Description}"));
        Console.WriteLine(FormattableString.Invariant(
            $"Client to server: {Describe(profile.ClientToServer)}. Server to client: {Describe(profile.ServerToClient)}. Ctrl+C stops it."));
        while (!stopped.Wait(reportInterval == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(reportInterval)))
        {
            Console.WriteLine(proxy.Progress());
        }

        proxy.Dispose();
        var report = proxy.Capture();
        report.Write(Console.Out);
        if (countsPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(countsPath)!);
            File.WriteAllText(countsPath, report.ToJson());
            Console.WriteLine($"Proxy counts: {countsPath}");
        }

        return 0;
    }

    private static int RunSelfTest(ProxyArguments arguments)
    {
        var defaults = SelfTestOptions.Default;
        var output = arguments.Optional("output");
        var options = new SelfTestOptions(
            arguments.Double("delay-ms", defaults.DelayMs, 0, 10_000),
            arguments.Int("seconds", defaults.Seconds, 1, 3600),
            arguments.Int("clients", defaults.Clients, 1, 256),
            arguments.Int("rate", defaults.PacketsPerSecondPerClient, 1, 10_000),
            arguments.Int("size", defaults.PacketBytes, 26, 1400),
            output is null ? null : Path.GetFullPath(output, Directory.GetCurrentDirectory()))
        {
            Spin = TimeSpan.FromMilliseconds(arguments.Double("spin-ms", PacketScheduler.DefaultSpin.TotalMilliseconds, 0, 10))
        };
        return SelfTest.Run(options, Console.Out).Passed ? 0 : 1;
    }

    private static string Describe(DirectionProfile direction)
    {
        var parts = new List<string>
        {
            FormattableString.Invariant($"{direction.DelayMs:0.###} ms +- {direction.JitterMs:0.###} ms"),
            FormattableString.Invariant($"{direction.LossPercent:0.###} % loss")
        };
        if (direction.BurstLoss is { } burst)
        {
            parts.Add(FormattableString.Invariant($"{burst.SharePercent:0.###} % of it in bursts of {burst.AverageLengthPackets:0.###} packets"));
        }

        if (direction.DuplicatePercent > 0)
        {
            parts.Add(FormattableString.Invariant($"{direction.DuplicatePercent:0.###} % duplicated"));
        }

        if (direction.Outages is { } outages)
        {
            parts.Add(FormattableString.Invariant($"down {outages.DurationSeconds:0.###} s every {outages.EverySeconds:0.###} s from {outages.FirstAfterSeconds:0.###} s"));
        }

        return string.Join(", ", parts);
    }
}

// The proxy's options: --name value pairs and bare flags, each at most once, none unknown.
internal sealed class ProxyArguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "allow-non-loopback" };
    private readonly Dictionary<string, string?> values;

    private ProxyArguments(Dictionary<string, string?> values)
    {
        this.values = values;
    }

    public static ProxyArguments Parse(string[] args, IReadOnlyCollection<string> allowed)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
            {
                throw new NetworkImpairmentException($"Unknown argument '{argument}'.");
            }

            var key = argument[2..];
            if (!allowed.Contains(key))
            {
                throw new NetworkImpairmentException($"Unknown option '--{key}'.");
            }

            string? value = null;
            if (!Flags.Contains(key))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new NetworkImpairmentException($"Option '--{key}' requires a value.");
                }

                value = args[++index];
            }

            if (!values.TryAdd(key, value))
            {
                throw new NetworkImpairmentException($"Option '--{key}' was given more than once.");
            }
        }

        return new ProxyArguments(values);
    }

    // An IP address and a port; localhost stands for 127.0.0.1. Names are not resolved, so the
    // loopback check sees the address that is used.
    public static IPEndPoint ParseEndPoint(string value, string key)
    {
        var text = value.Trim();
        if (text.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
        {
            text = "127.0.0.1" + text["localhost".Length..];
        }

        if (!IPEndPoint.TryParse(text, out var endPoint) || endPoint.Port is < 0 or > 65_535 || !text.Contains(':', StringComparison.Ordinal))
        {
            throw new NetworkImpairmentException($"--{key} must be an IP address and a port, such as 127.0.0.1:27115, not '{value}'.");
        }

        return endPoint;
    }

    public string Require(string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new NetworkImpairmentException($"--{key} is required.");
    }

    public string? Optional(string key)
    {
        return values.GetValueOrDefault(key);
    }

    public bool Flag(string key)
    {
        return values.ContainsKey(key);
    }

    public int Int(string key, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value
            : throw new NetworkImpairmentException($"--{key} must be a whole number from {minimum} to {maximum}.");
    }

    public long Long(string key, long fallback)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new NetworkImpairmentException($"--{key} must be a whole number.");
    }

    public double Double(string key, double fallback, double minimum, double maximum)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value
            : throw new NetworkImpairmentException($"--{key} must be a number from {minimum.ToString(CultureInfo.InvariantCulture)} to {maximum.ToString(CultureInfo.InvariantCulture)}.");
    }
}
