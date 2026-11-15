using System.Globalization;
using System.Text.Json;

namespace Mmorpg.Tools.StackStressGenerator;

// The GameService outage of a run: At into the steady interval the generator asks
// tools/Test-Load.ps1 to stop GameService, and the script starts it again Duration after the stop.
// The two exchange files in Folder: the generator writes the request, the script the window it
// measured, which the generator reads when the run ends.
public sealed record StressOutageOptions(TimeSpan At, TimeSpan Duration, string Folder)
{
    public const string RequestFileName = "outage-request.json";
    public const string WindowFileName = "outage-window.json";

    // The worker stops after 120 s without GameService by default, so an outage that must not
    // stop it stays well below that, with time for the return.
    public const int MinimumSeconds = 10;
    public const int MaximumSeconds = 100;

    // The steady interval goes on this long after the planned return, so the return, the first
    // durable actions after it, and the kills' corpses are measured inside it.
    public const int MinimumSecondsAfter = 60;

    public string RequestPath => Path.Combine(Folder, RequestFileName);

    public string WindowPath => Path.Combine(Folder, WindowFileName);
}

// What the script measured: when it sent GameService its Ctrl+C, when the process had exited,
// when it started the new process, and when that one answered its readiness check, in UTC.
public sealed record StressOutageWindow(
    DateTime? RequestedAt,
    DateTime? StoppedAt,
    DateTime? ExitedAt,
    DateTime? RestartedAt,
    DateTime? ReadyAt)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // The window, or null with the reason when the script reported none or a broken one.
    public static StressOutageWindow? TryRead(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            problem = $"tools/Test-Load.ps1 reported no outage window in {path}.";
            return null;
        }

        try
        {
            var window = JsonSerializer.Deserialize<StressOutageWindow>(File.ReadAllText(path), SerializerOptions);
            if (window?.StoppedAt is null || window.ReadyAt is null || window.ReadyAt < window.StoppedAt)
            {
                problem = $"The outage window in {path} lacks the stop or the ready time.";
                return null;
            }

            return window with
            {
                StoppedAt = Utc(window.StoppedAt),
                ExitedAt = Utc(window.ExitedAt),
                RestartedAt = Utc(window.RestartedAt),
                ReadyAt = Utc(window.ReadyAt),
                RequestedAt = Utc(window.RequestedAt)
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"The outage window in {path} cannot be read: {exception.Message}";
            return null;
        }
    }

    // Whether a moment lies between the stop and the moment GameService was ready again.
    public bool Contains(DateTime at)
    {
        return StoppedAt is { } stopped && ReadyAt is { } ready && at >= stopped && at <= ready;
    }

    private static DateTime? Utc(DateTime? value)
    {
        return value?.ToUniversalTime();
    }
}

// The answers that GameService's absence explains: the worker's own refusal while it does not
// answer, and the failures of a call that was on its way to GameService when it stopped.
public static class StressOutageCodes
{
    public const string Unavailable = "game_service_unavailable";
    public const string Timeout = "auth_service_timeout";
    public const string NoAnswer = "auth_service_unavailable";

    // The stop after the worker's outage tolerance, which a run of this tool never reaches.
    public const string OutageStop = "game_service_outage";

    // An HTTP read of a bot's items that GameService did not answer.
    public const string InventoryRefreshFailed = "inventory_refresh_failed";
    public const string InventoryRefresh = "inventory_refresh";

    public static bool IsOutageCode(string? code)
    {
        return code is Unavailable or Timeout or NoAnswer;
    }
}

// What one bot met around the outage. Its own poll thread writes it; the run reads it once the
// poll threads stopped.
public sealed class StressOutageBot(int botIndex)
{
    // A kill while saving was paused waits this long for its Mob's corpse to show near the killer.
    public static readonly TimeSpan KillCorpseWait = TimeSpan.FromSeconds(10);
    private const float KillCorpseRadius = 5f;

    // The corpse and the kill event travel on one reliable stream, so the bot can see the corpse
    // a moment before it reads the kill.
    private static readonly TimeSpan CorpseBeforeKillTolerance = TimeSpan.FromSeconds(1);

    private readonly List<StressOutageFailure> failures = [];
    private readonly List<PendingKill> pendingKills = [];

    // When the bot first saw each Mob corpse, and the corpses that already stand for a kill.
    private readonly Dictionary<Guid, DateTime> corpseFirstSeen = [];
    private readonly HashSet<Guid> matchedCorpses = [];

    public int BotIndex { get; } = botIndex;

    // GameService does not answer the bot's worker, as its last notice said.
    public bool SavingPaused { get; private set; }

    public int UnavailableNotices { get; private set; }

    public int AvailableNotices { get; private set; }

    public DateTime? FirstJoinedAt { get; private set; }

    public DateTime? FirstDurableSuccessAfterReturn { get; private set; }

    public int KillsWhilePaused { get; private set; }

    public int KillCorpsesSeen { get; private set; }

    public int KillCorpsesMissing { get; private set; }

    public IReadOnlyList<StressOutageFailure> Failures => failures;

    public void OnJoined(DateTime at)
    {
        FirstJoinedAt ??= at;
    }

    public void OnNotice(bool available)
    {
        if (available)
        {
            AvailableNotices++;
        }
        else
        {
            UnavailableNotices++;
        }

        SavingPaused = !available;
    }

    // A refusal or failure that the outage may explain, judged against the window once the run
    // ended, with the time it took when it answered an intent.
    public void RecordFailure(string operation, string code, DateTime at, double? elapsedMilliseconds = null)
    {
        failures.Add(new StressOutageFailure(operation, code, at, SavingPaused, elapsedMilliseconds));
    }

    // A committed durable action; the first one after the bot's available notice measures how
    // soon GameService's return reached the players.
    public void RecordDurableSuccess(DateTime at)
    {
        if (AvailableNotices > 0 && !SavingPaused && FirstDurableSuccessAfterReturn is null)
        {
            FirstDurableSuccessAfterReturn = at;
        }
    }

    // The bot killed a Mob of this definition at this place; while saving is paused its corpse
    // must still show. A Mob that already left the bot's view counts as a kill without the check.
    public void RecordKill(string? definitionId, float x, float z, DateTime at)
    {
        if (!SavingPaused)
        {
            return;
        }

        KillsWhilePaused++;
        if (!string.IsNullOrEmpty(definitionId))
        {
            pendingKills.Add(new PendingKill(definitionId, x, z, at, at + KillCorpseWait));
        }
    }

    // Takes the Mob corpses the bot sees now, every time it looks, so the tracker knows when the
    // bot first saw each of them, and matches the waiting kills in the order they happened. A
    // corpse stands for one kill only, and only when the bot first saw it after that kill, so
    // neither an older corpse of the same kind nearby nor the corpse of another kill can hide a
    // corpse that was never created. A kill whose wait ran out counts as missing.
    public void ObserveMobCorpses(
        IEnumerable<(Guid CorpseId, string DefinitionId, float X, float Z)> mobCorpses,
        DateTime now)
    {
        var corpses = mobCorpses as IReadOnlyCollection<(Guid CorpseId, string DefinitionId, float X, float Z)>
            ?? mobCorpses.ToArray();
        foreach (var corpse in corpses)
        {
            corpseFirstSeen.TryAdd(corpse.CorpseId, now);
        }

        for (var index = 0; index < pendingKills.Count; index++)
        {
            var kill = pendingKills[index];
            Guid? match = null;
            foreach (var corpse in corpses)
            {
                if (!matchedCorpses.Contains(corpse.CorpseId)
                    && string.Equals(corpse.DefinitionId, kill.DefinitionId, StringComparison.Ordinal)
                    && corpseFirstSeen[corpse.CorpseId] >= kill.At - CorpseBeforeKillTolerance
                    && MathF.Sqrt(((corpse.X - kill.X) * (corpse.X - kill.X)) + ((corpse.Z - kill.Z) * (corpse.Z - kill.Z))) <= KillCorpseRadius)
                {
                    match = corpse.CorpseId;
                    break;
                }
            }

            if (match is { } corpseId)
            {
                matchedCorpses.Add(corpseId);
                KillCorpsesSeen++;
                pendingKills.RemoveAt(index--);
            }
            else if (now >= kill.Deadline)
            {
                KillCorpsesMissing++;
                pendingKills.RemoveAt(index--);
            }
        }
    }

    public bool HasPendingKills => pendingKills.Count > 0;

    private sealed record PendingKill(string DefinitionId, float X, float Z, DateTime At, DateTime Deadline);
}

public sealed record StressOutageFailure(string Operation, string Code, DateTime At, bool SavingPaused, double? ElapsedMilliseconds);

// The outage's own numbers: when GameService was away, what the bots heard and met, and, once
// StackStressGenerator judge read the worker's log, when the worker noticed the outage and the
// return.
public sealed record StressOutageSummary(
    double AtSeconds,
    double PlannedSeconds,
    DateTime? RequestedAt,
    DateTime? StoppedAt,
    DateTime? RestartedAt,
    DateTime? ReadyAt,
    string? WindowProblem)
{
    // The time from the stop to GameService's readiness.
    public double? MeasuredSeconds => StoppedAt is { } stopped && ReadyAt is { } ready
        ? (ready - stopped).TotalSeconds
        : null;

    public int BotsAcrossOutage { get; init; }

    // Bots joined across the outage that heard exactly one unavailable and one available notice.
    public int BotsWithOneNoticeEach { get; init; }

    // The first bots that heard another number of notices, as "bot <index>: <u> unavailable, <a> available".
    public IReadOnlyList<string> OtherNotices { get; init; } = [];

    public long UnavailableNotices { get; init; }

    public long AvailableNotices { get; init; }

    // Refusals with game_service_unavailable between a bot's notices or inside the window, and
    // failures of calls that GameService stopped answering inside the window: expected.
    public long ExpectedRefusals { get; init; }

    public long ExpectedInFlightFailures { get; init; }

    public long ExplainedInventoryRefreshFailures { get; init; }

    public long ExplainedHttpFailures { get; init; }

    // The same answers outside the window, which the usual rules count, by "operation code".
    public long UnexplainedFailures { get; init; }

    public IReadOnlyDictionary<string, long> ExplainedCodes { get; init; } = new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> UnexplainedCodes { get; init; } = new Dictionary<string, long>();

    // How long the intents took that the outage's refusals and cut off calls answered; the
    // operations' latencies leave them out.
    public StressLatencySummary? ExpectedAnswerLatency { get; init; }

    public long KillsWhileSavingPaused { get; init; }

    public long KillCorpsesSeen { get; init; }

    public long KillCorpsesMissing { get; init; }

    // From GameService's readiness to the first durable action that committed after it.
    public double? FirstDurableSuccessAfterReadySeconds { get; init; }

    public int BotsWithDurableSuccessAfterReturn { get; init; }

    // From the worker's log, once judged: the stop to its "does not answer" warning, and the
    // readiness to its "answers again" line.
    public double? WorkerNoticedOutageSeconds { get; init; }

    public double? WorkerNoticedReturnSeconds { get; init; }

    public string? WorkerLogNote { get; init; }

    // The log lines inside this window are the outage's own: from the stop to the latest return
    // the outage_return_seconds rule accepts.
    public (DateTime Start, DateTime End)? LogWindow(double returnLimitSeconds)
    {
        return StoppedAt is { } stopped && ReadyAt is { } ready
            ? (stopped, ready.AddSeconds(returnLimitSeconds))
            : null;
    }
}

// The outage of one run: the request at its time, the bots' records, and their summary.
public sealed class StressOutageTracker(StressOutageOptions options)
{
    private const int KeptOtherNotices = 10;

    private readonly object gate = new();
    private readonly Dictionary<int, StressOutageBot> bots = [];
    private bool requested;

    public StressOutageOptions Options { get; } = options;

    public StressOutageBot Register(int botIndex)
    {
        lock (gate)
        {
            if (!bots.TryGetValue(botIndex, out var bot))
            {
                bot = new StressOutageBot(botIndex);
                bots.Add(botIndex, bot);
            }

            return bot;
        }
    }

    // Asks the script to stop GameService once the outage's time in the steady interval came.
    // The file appears whole, so the script never reads half of it.
    public bool RequestIfDue(TimeSpan intoSteady)
    {
        if (requested || intoSteady < Options.At)
        {
            return false;
        }

        requested = true;
        Directory.CreateDirectory(Options.Folder);
        var temporary = Options.RequestPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new { requestedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }));
        File.Move(temporary, Options.RequestPath, overwrite: true);
        Console.WriteLine(
            $"Asked tools/Test-Load.ps1 to stop GameService for {Options.Duration.TotalSeconds:0} s, {intoSteady.TotalSeconds:0} s into the steady interval.");
        return true;
    }

    // The summary once the poll threads stopped. Completed bots joined before the stop were
    // joined across the outage; HTTP failures inside the window are the outage's own.
    public StressOutageSummary Capture(
        IReadOnlyCollection<StressBotSnapshot> snapshots,
        IReadOnlyList<DateTime> httpFailureTimes)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(httpFailureTimes);
        var window = StressOutageWindow.TryRead(Options.WindowPath, out var problem);
        StressOutageBot[] tracked;
        lock (gate)
        {
            tracked = [.. bots.Values.OrderBy(bot => bot.BotIndex)];
        }

        var completed = snapshots
            .Where(snapshot => snapshot.State == StressBotState.Completed)
            .Select(snapshot => snapshot.BotIndex)
            .ToHashSet();
        var across = window is null
            ? []
            : tracked
                .Where(bot => completed.Contains(bot.BotIndex) && bot.FirstJoinedAt is { } joined && joined < window.StoppedAt)
                .ToArray();
        var other = across
            .Where(bot => bot.UnavailableNotices != 1 || bot.AvailableNotices != 1)
            .ToArray();

        long refusals = 0, inFlight = 0, refreshes = 0, unexplained = 0;
        var answerTimes = new List<double>();
        var explainedCodes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var unexplainedCodes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var failure in tracked.SelectMany(bot => bot.Failures))
        {
            var inside = window?.Contains(failure.At) == true;
            var explained = failure.Code == StressOutageCodes.Unavailable
                ? failure.SavingPaused || inside
                : inside;
            var key = failure.Operation + " " + failure.Code;
            if (!explained)
            {
                unexplained++;
                unexplainedCodes[key] = unexplainedCodes.GetValueOrDefault(key) + 1;
                continue;
            }

            explainedCodes[key] = explainedCodes.GetValueOrDefault(key) + 1;
            if (failure.ElapsedMilliseconds is { } elapsed)
            {
                answerTimes.Add(elapsed);
            }

            if (failure.Operation == StressOutageCodes.InventoryRefresh)
            {
                refreshes++;
            }
            else if (failure.Code == StressOutageCodes.Unavailable)
            {
                refusals++;
            }
            else
            {
                inFlight++;
            }
        }

        // The first success after a bot's available notice. The script learns of the readiness
        // by a poll every half second, so a bot can commit an action before the recorded
        // readiness; that is a recovery of zero seconds, not none.
        var firstSuccess = window?.ReadyAt is not null
            ? tracked
                .Select(bot => bot.FirstDurableSuccessAfterReturn)
                .OfType<DateTime>()
                .DefaultIfEmpty(DateTime.MinValue)
                .Min()
            : DateTime.MinValue;
        return new StressOutageSummary(
            Options.At.TotalSeconds,
            Options.Duration.TotalSeconds,
            window?.RequestedAt,
            window?.StoppedAt,
            window?.RestartedAt,
            window?.ReadyAt,
            problem)
        {
            BotsAcrossOutage = across.Length,
            BotsWithOneNoticeEach = across.Length - other.Length,
            OtherNotices = other
                .Take(KeptOtherNotices)
                .Select(bot => $"bot {bot.BotIndex}: {bot.UnavailableNotices} unavailable, {bot.AvailableNotices} available")
                .ToArray(),
            UnavailableNotices = tracked.Sum(bot => (long)bot.UnavailableNotices),
            AvailableNotices = tracked.Sum(bot => (long)bot.AvailableNotices),
            ExpectedRefusals = refusals,
            ExpectedInFlightFailures = inFlight,
            ExplainedInventoryRefreshFailures = refreshes,
            ExplainedHttpFailures = window is null ? 0 : httpFailureTimes.Count(window.Contains),
            UnexplainedFailures = unexplained,
            ExplainedCodes = explainedCodes,
            UnexplainedCodes = unexplainedCodes,
            ExpectedAnswerLatency = StressLatencySummary.Create(answerTimes),
            KillsWhileSavingPaused = tracked.Sum(bot => (long)bot.KillsWhilePaused),
            KillCorpsesSeen = tracked.Sum(bot => (long)bot.KillCorpsesSeen),
            KillCorpsesMissing = tracked.Sum(bot => (long)bot.KillCorpsesMissing),
            FirstDurableSuccessAfterReadySeconds = window?.ReadyAt is { } readyAt && firstSuccess != DateTime.MinValue
                ? Math.Max(0, (firstSuccess - readyAt).TotalSeconds)
                : null,
            BotsWithDurableSuccessAfterReturn = tracked.Count(bot => bot.FirstDurableSuccessAfterReturn is not null)
        };
    }
}
