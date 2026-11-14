using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.StackStressGenerator;

// The shared surface of every stress bot that the run coordinator polls.
public interface IStressBot : IDisposable
{
    StressBotState State { get; }

    bool IsGameplayIdle { get; }

    void Start();

    void Poll(long nowTimestamp, CancellationToken cancellationToken);

    void BeginLeave(long nowTimestamp);

    void BeginGameplayDrain();

    void BeginRemoteCadenceWindow(long nowTimestamp);

    void EndRemoteCadenceWindow(long nowTimestamp);

    StressBotSnapshot Capture();

    // Safe to call from another thread than the one that polls the bot.
    StressBotProgress CaptureProgress();
}

public readonly record struct StressBotProgress(
    StressBotState State,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots)
{
    public static StressBotProgress From(SimulationBotProgress progress)
    {
        return new StressBotProgress(
            (StressBotState)progress.State,
            progress.SnapshotsReceived,
            progress.EstimatedMissingSnapshots);
    }
}

// What the playing bots of one run share: their assignments, the item catalog they read like a
// client, the navigation, and the metrics.
public sealed record PlayingRunContext(
    PlayingScenario Scenario,
    IReadOnlyDictionary<int, PlayingAssignment> Assignments,
    PlayingGameData GameData,
    SimulationBotNavigation? Navigation,
    PlayingMetrics Metrics);

// One bot of the playing workload: a headless client with a world view, the body that sends
// its intents like a Unity client and measures them, and the state machine of its role, which
// thinks every few hundred milliseconds.
public sealed class PlayingStressBot : IStressBot, ISimulationBotRealtimeObserver, ISimulationBotInputSource
{
    // A decision follows the previous one after this long, drawn per bot from the seed.
    public static readonly TimeSpan MinimumThinkDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumThinkDelay = TimeSpan.FromMilliseconds(650);

    private readonly HeadlessSimulationBotClient client;
    private readonly PlayingBotBody body;
    private readonly IPlayingRole role;
    private readonly Random thinkRandom;
    private readonly long createdTimestamp = Stopwatch.GetTimestamp();
    private long nextThinkTimestamp;
    private bool draining;

    public PlayingStressBot(
        StressGeneratorOptions options,
        StressBotAdmission admission,
        PlayingRunContext context,
        Action<double> recordInputAcknowledgement,
        StressGameplayMetrics gameplayMetrics,
        Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(context);
        var assignment = context.Assignments[admission.BotIndex];
        var world = new SimulationBotWorldView(admission.CharacterName);
        if (admission.Inventory is { } inventory)
        {
            world.Items.Load(PlayingBotBody.ToItemSnapshot(inventory));
        }

        client = new HeadlessSimulationBotClient(
            new SimulationBotConnectionOptions(admission.WorkerHost, admission.WorkerUdpPort, options.JoinTimeout),
            new SimulationBotIdentity(
                admission.BotIndex,
                admission.AccountId,
                admission.CharacterId,
                admission.CharacterName,
                admission.Ticket,
                admission.ShardId,
                admission.WorldId),
            this,
            recordInputAcknowledgement,
            this,
            world);
        // The kit is what the kit fixture granted, as the bot's inventory shows it.
        var kit = PlayingRoster.ResolveKit(
            assignment.Kit,
            world.Items.Items.Select(item => item.DefinitionId),
            context.GameData.ItemCatalog,
            requireWeapon: assignment.Role == PlayingRole.Fighter);
        body = new PlayingBotBody(
            admission.BotIndex,
            assignment.Role,
            client,
            world,
            kit,
            context.GameData,
            context.Navigation,
            gameplayMetrics,
            context.Metrics,
            options.GameplayOperationTimeout,
            refreshInventory);
        var decisions = new Random(assignment.DecisionSeed);
        thinkRandom = new Random(PlayingRoster.Mix(assignment.DecisionSeed, 0x5448494e));
        var town = context.Scenario.GroundsOf(PlayingGroundKind.Town).FirstOrDefault();
        role = assignment.Role switch
        {
            PlayingRole.Fighter => new FighterRole(
                assignment.Home ?? throw new InvalidOperationException($"Fighter bot {admission.BotIndex} has no hunting ground."),
                context.Scenario.TeleportToGrounds,
                decisions,
                town),
            PlayingRole.Gatherer => new GathererRole(
                assignment.Home ?? throw new InvalidOperationException($"Gatherer bot {admission.BotIndex} has no node field."),
                context.Scenario.TeleportToGrounds,
                decisions,
                town),
            PlayingRole.Trader => new TraderRole(
                assignment.Home ?? throw new InvalidOperationException($"Trader bot {admission.BotIndex} has no town."),
                context.Scenario.TeleportToGrounds,
                decisions),
            PlayingRole.Walker => new WalkerRole(assignment.Home, context.Scenario.TeleportToGrounds, decisions),
            _ => throw new InvalidOperationException($"The role {assignment.Role} is not implemented.")
        };
    }

    public StressBotState State => (StressBotState)client.State;

    public bool IsGameplayIdle => body.IsIdle;

    public IPlayingRole Role => role;

    public void Start()
    {
        client.Start();
    }

    public void Poll(long nowTimestamp, CancellationToken cancellationToken)
    {
        client.Poll(nowTimestamp);
        body.Update(nowTimestamp, cancellationToken);
        if (draining
            || client.State != SimulationBotClientState.Joined
            || nowTimestamp < nextThinkTimestamp)
        {
            return;
        }

        role.Think(body, Stopwatch.GetElapsedTime(createdTimestamp, nowTimestamp));
        body.AfterThink();
        var delay = MinimumThinkDelay
            + TimeSpan.FromTicks((long)((MaximumThinkDelay - MinimumThinkDelay).Ticks * thinkRandom.NextDouble()));
        nextThinkTimestamp = nowTimestamp + (long)Math.Ceiling(delay.TotalSeconds * Stopwatch.Frequency);
    }

    public void BeginLeave(long nowTimestamp)
    {
        client.BeginLeave(nowTimestamp);
    }

    public void BeginGameplayDrain()
    {
        draining = true;
        body.Stand();
    }

    public void BeginRemoteCadenceWindow(long nowTimestamp)
    {
        client.BeginRemoteCadenceWindow(nowTimestamp);
    }

    public void EndRemoteCadenceWindow(long nowTimestamp)
    {
        client.EndRemoteCadenceWindow(nowTimestamp);
    }

    public StressBotSnapshot Capture()
    {
        var snapshot = client.Capture();
        return new StressBotSnapshot(
            snapshot.BotIndex,
            snapshot.CharacterName,
            (StressBotState)snapshot.State,
            snapshot.JoinLatencyMs,
            snapshot.PacketsSent,
            snapshot.BytesSent,
            snapshot.PacketsReceived,
            snapshot.BytesReceived,
            snapshot.SnapshotsReceived,
            snapshot.EstimatedMissingSnapshots,
            snapshot.SpawnPackets,
            snapshot.DespawnPackets,
            snapshot.UnacknowledgedInputsDropped,
            snapshot.LatestServerTick,
            snapshot.FailureCode,
            snapshot.FailureMessage,
            snapshot.RemoteCadence);
    }

    public StressBotProgress CaptureProgress()
    {
        return StressBotProgress.From(client.CaptureProgress());
    }

    public void Dispose()
    {
        client.Dispose();
    }

    public RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        return body.CurrentPath;
    }

    public void OnJoined(RealtimeJoinAccepted session)
    {
        body.OnJoined(session);
    }

    public void OnItemOperationResult(RealtimeItemOperationResult result)
    {
        body.Complete(
            result.OperationId,
            result.Succeeded,
            result.Error?.Code,
            result.RequiresInventoryRefresh,
            result.CarryState?.ItemStateRevision);
    }

    public void OnCorpsePresence(RealtimeCorpsePresenceSnapshotChunk chunk)
    {
    }

    public void OnCorpseInteractionResult(RealtimeCorpseInteractionResult result)
    {
        body.Complete(
            result.OperationId,
            result.Succeeded,
            result.Error?.Code,
            result.RequiresInventoryRefresh,
            result.CarryState?.ItemStateRevision);
    }

    public void OnCorpseViewState(RealtimeCorpseViewStateChunk chunk)
    {
    }

    public void OnCorpseViewClosed(RealtimeCorpseViewClosed closed)
    {
    }

    public void OnCombatIntentResult(RealtimeCombatIntentResult result)
    {
        body.Complete(result.OperationId, result.Succeeded, result.Error?.Code, false, null);
    }

    public void OnAdminIntentResult(RealtimeAdminIntentResult result)
    {
        body.Complete(result.OperationId, result.Succeeded, result.Error?.Code, false, null);
    }

    public void OnWorldInteractionOpened(RealtimeWorldInteractionOpened opened)
    {
        body.OnInteractionOpened(opened);
    }

    public void OnWorldInteractionResult(RealtimeWorldInteractionResult result)
    {
        // A successful Open is answered by the opened message; a successful Close also sends a
        // result, which completes the close.
        if (result.Succeeded && result.OperationKind == RealtimeWorldInteractionOperationKind.Open)
        {
            return;
        }

        body.OnInteractionResult(result);
    }

    public void OnGatheringState(RealtimeGatheringState state)
    {
        body.OnGathering(state);
    }
}
