using System.Diagnostics.Metrics;

namespace Mmorpg.SimulationWorker.GameServiceApi;

// How the worker's calls to GameService fail, and when the worker holds back its registration.
// The failure counter has two fixed labels, so it never has more than a few dozen series: the
// route group, which RouteGroupOf names from the operation, and the reason.
public sealed class GameServiceCallMetrics : IDisposable
{
    public const string MeterName = "Mmorpg.SimulationWorker.GameService.Calls";

    // The reasons that a call is counted as failed. A refusal that GameService itself made, such
    // as a taken slot or a rejected ticket, is the call working and is not counted, unless it is
    // a server error.
    public const string TimeoutReason = "timeout";
    public const string UnreachableReason = "unreachable";
    public const string ServerErrorReason = "server_error";
    public const string NoProblemReason = "no_problem";
    public const string InvalidResponseReason = "invalid_response";
    public const string AuthenticationReason = "authentication";

    public const string OtherRouteGroup = "other";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> callFailures;
    private readonly Counter<long> renewalsWithheld;

    public GameServiceCallMetrics()
    {
        callFailures = meter.CreateCounter<long>("simulation_worker.game_service.call_failures");
        renewalsWithheld = meter.CreateCounter<long>("simulation_worker.registry.renewals_withheld");

        // The series exists as zero from the start, so the first withheld renewal is an increase
        // that an alert can see.
        renewalsWithheld.Add(0);
    }

    // Operation is the name that GameServiceClient gives the call; the group is registry, session,
    // join, checkpoint, item, corpse, quest, or appearance.
    public void RecordCallFailure(string operation, string reason)
    {
        callFailures.Add(
            1,
            new KeyValuePair<string, object?>("route", RouteGroupOf(operation)),
            new KeyValuePair<string, object?>("reason", reason));
    }

    // One per episode in which the worker stopped renewing its registration because its realtime
    // loop was silent.
    public void RecordRenewalsWithheld()
    {
        renewalsWithheld.Add(1);
    }

    public static string RouteGroupOf(string operation)
    {
        return operation switch
        {
            "GameService.HeartbeatSimulationWorker"
                or "GameService.MarkSimulationWorkerOffline" => "registry",
            "GameService.HeartbeatSimulationSessions"
                or "GameService.ReleaseSimulationSession" => "session",
            "GameService.ConsumeJoinTicket" => "join",
            "GameService.CheckpointCharacterStates" => "checkpoint",
            "GameService.MutateSimulationItems"
                or "GameService.ConsumeAmmunition"
                or "GameService.ProcessPlayerDeath" => "item",
            "GameService.RestoreDurableCorpses"
                or "GameService.CreatePersistentMobCorpse"
                or "GameService.GrantLiveMobLoot"
                or "GameService.GrantLiveMobLootAll"
                or "GameService.OpenCorpse"
                or "GameService.MutateCorpse" => "corpse",
            "GameService.ResolveQuestDrops"
                or "GameService.MutateSimulationQuests"
                or "GameService.ReadSimulationQuestLog" => "quest",
            "GameService.ChangeAppearance" => "appearance",
            _ => OtherRouteGroup
        };
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
