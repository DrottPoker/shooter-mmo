using System.Globalization;
using System.Security.Cryptography;

namespace Mmorpg.Tools.DeploySmokeTest;

// The settings of one smoke test command, from `--name value` pairs after the command.
public sealed record SmokeOptions(
    Uri BaseUrl,
    string CaPath,
    string ShardId,
    string WorkerId,
    string? ExpectedEndpoint,
    int Accounts,
    TimeSpan Walk,
    TimeSpan Timeout,
    string RunId)
{
    public const string Usage =
        "Usage: DeploySmokeTest <play|hold|watch-ready> --url https://localhost:8443 --ca <root.crt>"
        + " [--shard local-shard-1] [--worker-id deploy-worker-1] [--expected-endpoint 127.0.0.1:28015]"
        + " [--accounts 3] [--walk-seconds 30] [--timeout-seconds 600] [--run-id <letters and digits>]";

    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static SmokeOptions Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                throw new ArgumentException($"{args[index]} needs a value. {Usage}");
            }

            values[args[index][2..]] = args[index + 1];
        }

        var url = Required(values, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("--url must be an absolute https URL: the stack is reached through its TLS proxy.");
        }

        var runId = values.GetValueOrDefault("run-id") ?? RandomNumberGenerator.GetString(IdAlphabet, 6);
        if (runId.Length is < 1 or > 12 || !runId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("--run-id must be 1 to 12 letters and digits.");
        }

        return new SmokeOptions(
            baseUrl,
            Required(values, "ca"),
            values.GetValueOrDefault("shard") ?? "local-shard-1",
            values.GetValueOrDefault("worker-id") ?? "deploy-worker-1",
            values.GetValueOrDefault("expected-endpoint"),
            Integer(values, "accounts", 3, 1, 4),
            TimeSpan.FromSeconds(Integer(values, "walk-seconds", 30, 1, 600)),
            TimeSpan.FromSeconds(Integer(values, "timeout-seconds", 600, 1, 3600)),
            runId.ToLowerInvariant());
    }

    private static string Required(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required. {Usage}");
    }

    private static int Integer(Dictionary<string, string> values, string name, int fallback, int minimum, int maximum)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= minimum
            && value <= maximum
            ? value
            : throw new ArgumentException($"--{name} must be a whole number from {minimum} to {maximum}.");
    }
}
