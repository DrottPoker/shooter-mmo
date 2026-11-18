using System.Diagnostics;
using System.Net;

namespace Mmorpg.Tools.DeploySmokeTest;

// Watches /health/ready through the proxy while GameService stops, to see what a proxy and its
// clients see: ready first, then the status stopping with a 503 while GameService still serves,
// and then nothing that answers as GameService, because the proxy took it out of rotation. It
// prints WATCHING after the first ready answer, so the caller knows when to stop GameService, and
// ends when the proxy no longer reaches it.
public sealed class ReadyWatcher(SmokeOptions options, StackApi api, SmokeReport report, TextWriter output)
{
    public const string WatchingMarker = "WATCHING";

    private enum Seen
    {
        Ready,
        Stopping,
        Other
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await report.StepAsync("watch", () => WatchAsync(cancellationToken));
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(options.Timeout.TotalSeconds * Stopwatch.Frequency);
        long? firstReady = null;
        long? firstStopping = null;
        long? lastStopping = null;
        long? gone = null;
        var announced = false;

        while (Stopwatch.GetTimestamp() < deadline && gone is null)
        {
            var seen = await ProbeAsync(cancellationToken);
            var now = Stopwatch.GetTimestamp();
            switch (seen)
            {
                case Seen.Ready:
                    firstReady ??= now;
                    if (!announced)
                    {
                        announced = true;
                        output.WriteLine(WatchingMarker);
                        output.Flush();
                    }

                    break;
                case Seen.Stopping:
                    firstStopping ??= now;
                    lastStopping = now;
                    break;
                default:
                    // Nothing that is GameService answers: the proxy has no upstream left, or the
                    // connection failed. Before the stop this is not the end, only not yet started.
                    if (firstStopping is not null)
                    {
                        gone = now;
                    }

                    break;
            }

            await Task.Delay(200, cancellationToken);
        }

        report.Check(
            "ready before the stop",
            firstReady is not null,
            firstReady is null ? "GameService never answered ready" : "GameService answered ready through the proxy");
        report.Check(
            "unready first",
            firstStopping is not null,
            firstStopping is null
                ? "GameService never answered 503 stopping"
                : $"GameService answered 503 stopping for {Seconds(firstStopping.Value, lastStopping!.Value):0.0} s while it still served");
        report.Check(
            "out of rotation after it was unready",
            gone is not null && firstStopping is not null && gone > firstStopping,
            gone is null
                ? "GameService still answered when the time ran out"
                : $"the proxy stopped sending requests to GameService {Seconds(firstStopping!.Value, gone.Value):0.0} s after the first 503 stopping");
    }

    private async Task<Seen> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await api.SendAsync(HttpMethod.Get, "/health/ready", cancellationToken: cancellationToken);
            var status = StackJson.Text(response.Body, "status");
            if (response.Status == HttpStatusCode.OK && status == "ready")
            {
                return Seen.Ready;
            }

            return response.Status == HttpStatusCode.ServiceUnavailable && status == "stopping" ? Seen.Stopping : Seen.Other;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Seen.Other;
        }
    }

    private static double Seconds(long from, long to)
    {
        return Stopwatch.GetElapsedTime(from, to).TotalSeconds;
    }
}
