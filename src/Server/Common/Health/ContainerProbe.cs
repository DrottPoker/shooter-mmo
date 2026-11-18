using System.Globalization;

namespace Mmorpg.Server.Common.Health;

// The health check of a container whose image has no shell, curl, or wget: the service's own
// binary answers it, started as `<service> probe ...` before any host or configuration exists,
// so it stays cheap enough to run every few seconds.
//
//   probe http <url> [timeoutSeconds]   healthy when the URL answers with a 2xx status
//   probe file <path> <maxAgeSeconds>   healthy when the file was written within the age
//
// The exit code is 0 when healthy and 1 otherwise, which is what a container runtime reads, and
// one line says why.
public static class ContainerProbe
{
    public const string Command = "probe";
    public const int DefaultTimeoutSeconds = 3;
    public const int MaximumTimeoutSeconds = 60;

    public static bool IsProbe(IReadOnlyList<string> args)
    {
        return args.Count > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (!IsProbe(args) || args.Count < 3)
        {
            await output.WriteLineAsync("Usage: probe http <url> [timeoutSeconds] | probe file <path> <maxAgeSeconds>");
            return 1;
        }

        var kind = args[1];
        if (string.Equals(kind, "http", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeHttpAsync(args, output, cancellationToken, handler);
        }

        if (string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeFileAsync(args, output, timeProvider ?? TimeProvider.System);
        }

        await output.WriteLineAsync($"Unknown probe '{kind}'; use http or file.");
        return 1;
    }

    private static async Task<int> ProbeHttpAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler)
    {
        if (!Uri.TryCreate(args[2], UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
        {
            await output.WriteLineAsync("The probe URL must be an absolute http or https URL.");
            return 1;
        }

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (args.Count > 3
            && (!int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutSeconds)
                || timeoutSeconds is < 1 or > MaximumTimeoutSeconds))
        {
            await output.WriteLineAsync($"The probe timeout must be a whole number from 1 to {MaximumTimeoutSeconds} seconds.");
            return 1;
        }

        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var healthy = response.IsSuccessStatusCode;
            await output.WriteLineAsync(
                $"{(healthy ? "Healthy" : "Unhealthy")}: {url.Host}:{url.Port}{url.AbsolutePath} answered {(int)response.StatusCode}.");
            return healthy ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            // Only the host, port, and path are printed: a URL may carry a query that is no one's business.
            await output.WriteLineAsync(
                $"Unhealthy: {url.Host}:{url.Port}{url.AbsolutePath} did not answer within {timeoutSeconds} s: {exception.GetType().Name}.");
            return 1;
        }
    }

    private static async Task<int> ProbeFileAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TimeProvider timeProvider)
    {
        if (args.Count < 4
            || !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAgeSeconds)
            || maxAgeSeconds < 1)
        {
            await output.WriteLineAsync("The probe file needs a maximum age in whole seconds, at least 1.");
            return 1;
        }

        var path = args[2];
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            await output.WriteLineAsync($"Unhealthy: {path} does not exist.");
            return 1;
        }

        var age = timeProvider.GetUtcNow() - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var healthy = age <= TimeSpan.FromSeconds(maxAgeSeconds);
        await output.WriteLineAsync(
            healthy
                ? $"Healthy: {path} was written {Math.Max(0, (int)age.TotalSeconds)} s ago."
                : $"Unhealthy: {path} was written {(int)age.TotalSeconds} s ago, more than {maxAgeSeconds} s.");
        return healthy ? 0 : 1;
    }
}
