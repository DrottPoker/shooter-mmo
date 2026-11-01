using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.StackStressGenerator;

public enum StressBotState
{
    Created,
    Connecting,
    Joining,
    Joined,
    Leaving,
    Completed,
    Failed
}

public sealed class StressBot : IDisposable, ISimulationBotRealtimeObserver
{
    private readonly StressGeneratorOptions options;
    private readonly StressBotAdmission admission;
    private readonly StressGameplayMetrics gameplayMetrics;
    private readonly Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>>
        refreshInventory;
    private readonly HeadlessSimulationBotClient client;
    private readonly StressCorpseViewState corpseView = new();
    private StressInventoryState? inventory;
    private RealtimeJoinAccepted? joinedSession;
    private PendingGameplayOperation? pendingGameplay;
    private Task<FullStackCharacterInventoryResponse?>? inventoryRefreshTask;
    private bool inventoryRefreshRequested;
    private bool lootHotspotDiscovered;
    private bool corpseViewOpen;
    private bool corpseRefreshRequested;
    private bool gameplayDraining;
    private long nextInventoryOperationTimestamp;
    private long nextLootOperationTimestamp;

    public StressBot(
        StressGeneratorOptions options,
        StressBotAdmission admission,
        StressLatencyAccumulator inputAcknowledgementLatencies,
        StressLatencyAccumulator intervalInputAcknowledgementLatencies,
        StressGameplayMetrics gameplayMetrics,
        Func<CancellationToken, Task<FullStackCharacterInventoryResponse?>> refreshInventory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(inputAcknowledgementLatencies);
        ArgumentNullException.ThrowIfNull(intervalInputAcknowledgementLatencies);
        ArgumentNullException.ThrowIfNull(gameplayMetrics);
        ArgumentNullException.ThrowIfNull(refreshInventory);

        this.options = options;
        this.admission = admission;
        this.gameplayMetrics = gameplayMetrics;
        this.refreshInventory = refreshInventory;
        inventory = admission.Inventory is null
            ? null
            : new StressInventoryState(admission.Inventory);
        client = new HeadlessSimulationBotClient(
            new SimulationBotConnectionOptions(
                admission.WorkerHost,
                admission.WorkerUdpPort,
                options.JoinTimeout),
            new SimulationBotIdentity(
                admission.BotIndex,
                admission.AccountId,
                admission.CharacterId,
                admission.CharacterName,
                admission.Ticket,
                admission.ShardId,
                admission.WorldId),
            new StressBotInputSource(admission.BotIndex, options.Seed, options.Workload),
            latencyMilliseconds =>
            {
                inputAcknowledgementLatencies.Record(latencyMilliseconds);
                intervalInputAcknowledgementLatencies.Record(latencyMilliseconds);
            },
            this);
    }

    public StressBotState State => (StressBotState)client.State;

    public void Start()
    {
        client.Start();
    }

    public void Poll(long nowTimestamp, CancellationToken cancellationToken)
    {
        client.Poll(nowTimestamp);
        CompleteInventoryRefresh();
        if (pendingGameplay is not null
            && Stopwatch.GetElapsedTime(pendingGameplay.StartedTimestamp, nowTimestamp)
                >= options.GameplayOperationTimeout)
        {
            gameplayMetrics.RecordTimeout(pendingGameplay.OperationName);
            pendingGameplay = null;
            inventoryRefreshRequested = true;
            corpseRefreshRequested = corpseViewOpen;
        }

        if (client.State != SimulationBotClientState.Joined)
        {
            return;
        }

        if (inventoryRefreshRequested && inventoryRefreshTask is null)
        {
            inventoryRefreshRequested = false;
            inventoryRefreshTask = refreshInventory(cancellationToken);
        }

        if (pendingGameplay is not null || inventoryRefreshTask is not null)
        {
            return;
        }

        if (gameplayDraining)
        {
            return;
        }

        if (corpseRefreshRequested && corpseViewOpen && corpseView.IsReady)
        {
            corpseRefreshRequested = false;
            SendCorpseOperation(
                "corpse_refresh",
                RealtimeCorpseInteractionIntent.CreateRefresh(
                    Guid.NewGuid(),
                    admission.LootHotspotCorpseId!.Value,
                    corpseView.Revision),
                nowTimestamp);
            return;
        }

        switch (options.Workload)
        {
            case StressWorkloadProfile.Inventory:
                if (nowTimestamp >= nextInventoryOperationTimestamp)
                {
                    TrySendInventoryRelocate(nowTimestamp);
                }
                break;
            case StressWorkloadProfile.LootHotspot:
                if (nowTimestamp >= nextLootOperationTimestamp)
                {
                    TrySendLootOperation(nowTimestamp);
                }
                break;
            case StressWorkloadProfile.MixedGameplay:
                if (nowTimestamp >= nextInventoryOperationTimestamp
                    && nextInventoryOperationTimestamp <= nextLootOperationTimestamp)
                {
                    TrySendInventoryRelocate(nowTimestamp);
                }
                else if (nowTimestamp >= nextLootOperationTimestamp)
                {
                    TrySendLootOperation(nowTimestamp);
                }
                break;
        }
    }

    public void BeginLeave(long nowTimestamp)
    {
        client.BeginLeave(nowTimestamp);
    }

    public void BeginGameplayDrain()
    {
        gameplayDraining = true;
    }

    public bool IsGameplayIdle => pendingGameplay is null
        && inventoryRefreshTask is null;

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
            snapshot.FailureMessage);
    }

    public void Dispose()
    {
        client.Dispose();
    }

    public void OnJoined(RealtimeJoinAccepted session)
    {
        joinedSession = session;
        var now = Stopwatch.GetTimestamp();
        nextInventoryOperationTimestamp = AddStagger(
            now,
            options.InventoryOperationInterval,
            17);
        nextLootOperationTimestamp = AddStagger(
            now,
            options.LootOperationInterval,
            37);
    }

    public void OnItemOperationResult(RealtimeItemOperationResult result)
    {
        var pending = TakePending(result.OperationId, result.OperationKind);
        if (pending is null)
        {
            RecordUnexpectedResult("unexpected_item_result");
            return;
        }

        gameplayMetrics.RecordResult(
            pending.OperationName,
            Stopwatch.GetElapsedTime(pending.StartedTimestamp).TotalMilliseconds,
            result.Succeeded,
            result.Succeeded ? null : result.Error.Code,
            expectedRejection: false);
        if (result.Succeeded
            && pending.ItemIntent is not null
            && inventory is not null
            && !inventory.ApplyRelocate(pending.ItemIntent, result))
        {
            inventoryRefreshRequested = true;
        }
        else if (!result.Succeeded || result.RequiresInventoryRefresh)
        {
            inventoryRefreshRequested = true;
        }

        nextInventoryOperationTimestamp = AddDuration(
            Stopwatch.GetTimestamp(),
            options.InventoryOperationInterval);
    }

    public void OnCorpsePresence(RealtimeCorpsePresenceSnapshotChunk chunk)
    {
        if (admission.LootHotspotCorpseId is { } corpseId
            && chunk.Corpses.Any(corpse => corpse.CorpseId == corpseId && !corpse.IsEmpty))
        {
            lootHotspotDiscovered = true;
        }
    }

    public void OnCorpseInteractionResult(RealtimeCorpseInteractionResult result)
    {
        var pending = TakePending(result.OperationId, result.OperationKind);
        if (pending is null)
        {
            RecordUnexpectedResult("unexpected_corpse_result");
            return;
        }

        var code = result.Succeeded ? null : result.Error.Code;
        gameplayMetrics.RecordResult(
            pending.OperationName,
            Stopwatch.GetElapsedTime(pending.StartedTimestamp).TotalMilliseconds,
            result.Succeeded,
            code,
            !result.Succeeded && StressCorpseOutcomes.IsExpectedRejection(options.Workload, code));

        if (result.OperationKind == RealtimeCorpseInteractionKind.Open)
        {
            corpseViewOpen = result.Succeeded;
        }

        if (result.RequiresCorpseRefresh)
        {
            corpseRefreshRequested = corpseViewOpen;
        }

        if (result.RequiresInventoryRefresh)
        {
            inventoryRefreshRequested = true;
        }

        nextLootOperationTimestamp = AddDuration(
            Stopwatch.GetTimestamp(),
            options.LootOperationInterval);
    }

    public void OnCorpseViewState(RealtimeCorpseViewStateChunk chunk)
    {
        if (admission.LootHotspotCorpseId != chunk.CorpseId)
        {
            return;
        }

        var result = corpseView.Apply(chunk);
        if (result is CorpseViewApplyResult.Stale or CorpseViewApplyResult.Invalid)
        {
            corpseRefreshRequested = corpseViewOpen && corpseView.IsReady;
        }
    }

    public void OnCorpseViewClosed(RealtimeCorpseViewClosed closed)
    {
        if (admission.LootHotspotCorpseId != closed.CorpseId)
        {
            return;
        }

        corpseViewOpen = false;
        corpseView.Clear();
    }

    private void TrySendInventoryRelocate(long nowTimestamp)
    {
        if (inventory is null)
        {
            gameplayMetrics.RecordInventoryRefreshFailure();
            nextInventoryOperationTimestamp = AddDuration(
                nowTimestamp,
                options.InventoryOperationInterval);
            return;
        }

        var definitions = admission.FixtureDefinitions;
        var owned = definitions is null
            ? null
            : inventory.FindItem(definitions.InventoryDefinitionId);
        if (owned is null)
        {
            inventoryRefreshRequested = true;
            return;
        }

        // The item moves to the next empty Permanent Inventory slot each time.
        var destinationId = inventory.PermanentInventoryId;
        var destinationSlot = inventory.FindEmptySlot(destinationId);
        if (destinationSlot is null)
        {
            inventoryRefreshRequested = true;
            return;
        }

        var intent = RealtimeItemOperationIntent.CreateRelocate(
            Guid.NewGuid(),
            inventory.CharacterRevision,
            owned.Item.ItemInstanceId,
            owned.Item.Revision,
            destinationId,
            destinationSlot.Value);
        if (client.SendItemOperation(intent))
        {
            gameplayMetrics.RecordRequest("item_relocate");
            pendingGameplay = PendingGameplayOperation.ForItem(
                "item_relocate",
                intent,
                nowTimestamp);
        }
    }

    private void TrySendLootOperation(long nowTimestamp)
    {
        if (admission.LootHotspotCorpseId is null || joinedSession is null)
        {
            return;
        }

        if (!lootHotspotDiscovered)
        {
            nextLootOperationTimestamp = AddDuration(nowTimestamp, TimeSpan.FromSeconds(1));
            return;
        }

        if (!corpseViewOpen)
        {
            SendCorpseOperation(
                "corpse_open",
                RealtimeCorpseInteractionIntent.CreateOpen(
                    Guid.NewGuid(),
                    admission.LootHotspotCorpseId.Value),
                nowTimestamp);
            return;
        }

        if (!corpseView.IsReady || inventory is null)
        {
            return;
        }

        var lootDefinitionId = admission.FixtureDefinitions?.LootDefinitionId;
        if (lootDefinitionId is null)
        {
            return;
        }

        var ownedLoot = inventory.FindItem(lootDefinitionId);
        if (ownedLoot is not null)
        {
            var target = corpseView.FindLootStack(
                lootDefinitionId,
                requireAvailableCapacity: true);
            if (target is null)
            {
                corpseRefreshRequested = true;
                return;
            }

            SendCorpseOperation(
                "corpse_deposit",
                RealtimeCorpseInteractionIntent.CreateDepositItem(
                    Guid.NewGuid(),
                    admission.LootHotspotCorpseId.Value,
                    corpseView.Revision,
                    ownedLoot.Item.ItemInstanceId,
                    ownedLoot.Item.Revision,
                    target.ContainerId,
                    target.ContainerRevision,
                    target.SlotIndex,
                    target.Item.ItemInstanceId,
                    target.Item.Revision),
                nowTimestamp);
            return;
        }

        var loot = corpseView.FindLootStack(lootDefinitionId, requireAvailableCapacity: false);
        var destinationSlot = inventory.FindEmptySlot(inventory.PermanentInventoryId);
        if (loot is null || destinationSlot is null)
        {
            corpseRefreshRequested = true;
            inventoryRefreshRequested = destinationSlot is null;
            return;
        }

        var intent = loot.Item.Quantity > 1
            ? RealtimeCorpseInteractionIntent.CreateLootPartialStack(
                Guid.NewGuid(),
                admission.LootHotspotCorpseId.Value,
                corpseView.Revision,
                loot.Item.ItemInstanceId,
                loot.Item.Revision,
                1,
                inventory.PermanentInventoryId,
                inventory.GetContainerRevision(inventory.PermanentInventoryId),
                destinationSlot.Value,
                Guid.Empty,
                0)
            : RealtimeCorpseInteractionIntent.CreateLootItem(
                Guid.NewGuid(),
                admission.LootHotspotCorpseId.Value,
                corpseView.Revision,
                loot.Item.ItemInstanceId,
                loot.Item.Revision,
                inventory.PermanentInventoryId,
                inventory.GetContainerRevision(inventory.PermanentInventoryId),
                destinationSlot.Value,
                Guid.Empty,
                0);
        SendCorpseOperation(
            loot.Item.Quantity > 1 ? "corpse_loot_partial" : "corpse_loot_item",
            intent,
            nowTimestamp);
    }

    private void SendCorpseOperation(
        string operationName,
        RealtimeCorpseInteractionIntent intent,
        long nowTimestamp)
    {
        if (client.SendCorpseInteraction(intent))
        {
            gameplayMetrics.RecordRequest(operationName);
            pendingGameplay = PendingGameplayOperation.ForCorpse(
                operationName,
                intent,
                nowTimestamp);
        }
    }

    private PendingGameplayOperation? TakePending(
        Guid operationId,
        RealtimeItemOperationKind operationKind)
    {
        if (pendingGameplay?.ItemIntent is null
            || pendingGameplay.ItemIntent.OperationId != operationId
            || pendingGameplay.ItemIntent.OperationKind != operationKind)
        {
            return null;
        }

        var pending = pendingGameplay;
        pendingGameplay = null;
        return pending;
    }

    private PendingGameplayOperation? TakePending(
        Guid operationId,
        RealtimeCorpseInteractionKind operationKind)
    {
        if (pendingGameplay?.CorpseIntent is null
            || pendingGameplay.CorpseIntent.OperationId != operationId
            || pendingGameplay.CorpseIntent.OperationKind != operationKind)
        {
            return null;
        }

        var pending = pendingGameplay;
        pendingGameplay = null;
        return pending;
    }

    private void CompleteInventoryRefresh()
    {
        if (inventoryRefreshTask is null || !inventoryRefreshTask.IsCompleted)
        {
            return;
        }

        try
        {
            var snapshot = inventoryRefreshTask.GetAwaiter().GetResult();
            if (snapshot is null)
            {
                gameplayMetrics.RecordInventoryRefreshFailure();
            }
            else if (inventory is null)
            {
                inventory = new StressInventoryState(snapshot);
            }
            else
            {
                inventory.Apply(snapshot);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            gameplayMetrics.RecordInventoryRefreshFailure();
            Console.WriteLine(
                $"Bot {admission.BotIndex} inventory refresh failed: {exception.Message}");
        }
        finally
        {
            inventoryRefreshTask = null;
        }
    }

    private void RecordUnexpectedResult(string operation)
    {
        gameplayMetrics.RecordRequest(operation);
        gameplayMetrics.RecordResult(
            operation,
            0,
            succeeded: false,
            "unexpected_result",
            expectedRejection: false);
    }

    private long AddStagger(long timestamp, TimeSpan interval, int multiplier)
    {
        var fraction = ((admission.BotIndex * multiplier) % 100) / 100d;
        return AddDuration(timestamp, TimeSpan.FromTicks(
            Math.Max(1, (long)(interval.Ticks * fraction))));
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed record PendingGameplayOperation(
        string OperationName,
        long StartedTimestamp,
        RealtimeItemOperationIntent? ItemIntent,
        RealtimeCorpseInteractionIntent? CorpseIntent)
    {
        public static PendingGameplayOperation ForItem(
            string operationName,
            RealtimeItemOperationIntent intent,
            long startedTimestamp)
        {
            return new PendingGameplayOperation(
                operationName,
                startedTimestamp,
                intent,
                null);
        }

        public static PendingGameplayOperation ForCorpse(
            string operationName,
            RealtimeCorpseInteractionIntent intent,
            long startedTimestamp)
        {
            return new PendingGameplayOperation(
                operationName,
                startedTimestamp,
                null,
                intent);
        }
    }

    // Movement paths per workload. Hotspot bots stand at their corpses, mixed gameplay bots
    // step back and forth beside them, and the default workload walks a square loop.
    private sealed class StressBotInputSource(
        int botIndex,
        int seed,
        StressWorkloadProfile workload) : ISimulationBotInputSource
    {
        private const float MixedGameplayStep = 0.5f;
        private SimulationBotPosition? origin;
        private RealtimeMovementPath? currentPath;

        public RealtimeMovementPath? SelectPath(
            uint clientTick,
            RealtimeJoinAccepted joinedSession,
            SimulationBotPosition position)
        {
            origin ??= position;
            var start = origin.Value;
            var settings = joinedSession.MovementSettings;
            if (workload == StressWorkloadProfile.LootHotspot)
            {
                return null;
            }

            if (workload == StressWorkloadProfile.MixedGameplay)
            {
                var phaseTicks = Math.Max(1u, settings.TickRateHz / 3u);
                var step = (clientTick / phaseTicks) + (uint)botIndex;
                return (step % 4u) switch
                {
                    0 => PathFor(step, start.X, start.Z + MixedGameplayStep, start.Y, settings),
                    2 => PathFor(step, start.X, start.Z, start.Y, settings),
                    _ => null
                };
            }

            var ticksPerLeg = Math.Max(1u, settings.TickRateHz * 2u);
            var leg = (clientTick / ticksPerLeg) + (uint)(botIndex + seed);
            var legLength = settings.WalkSpeed * 2f;
            var (offsetX, offsetZ) = (leg % 4u) switch
            {
                0 => (0f, legLength),
                1 => (legLength, legLength),
                2 => (legLength, 0f),
                _ => (0f, 0f)
            };
            return PathFor(leg, start.X + offsetX, start.Z + offsetZ, start.Y, settings);
        }

        // One path id per step, so every tick of the same step follows the same path.
        private RealtimeMovementPath PathFor(
            uint step,
            float x,
            float z,
            float y,
            RealtimeMovementSettings settings)
        {
            var pathId = Math.Max(1u, unchecked(step + 1u));
            if (currentPath?.PathId != pathId)
            {
                currentPath = new RealtimeMovementPath(
                    pathId,
                    [
                        new RealtimeMovementPathWaypoint(
                            Math.Clamp(x, settings.MinimumX, settings.MaximumX),
                            y,
                            Math.Clamp(z, settings.MinimumZ, settings.MaximumZ))
                    ]);
            }

            return currentPath;
        }
    }
}

public sealed record StressBotSnapshot(
    int BotIndex,
    string CharacterName,
    StressBotState State,
    double? JoinLatencyMs,
    long PacketsSent,
    long BytesSent,
    long PacketsReceived,
    long BytesReceived,
    long SnapshotsReceived,
    long EstimatedMissingSnapshots,
    long SpawnPackets,
    long DespawnPackets,
    long UnacknowledgedInputsDropped,
    uint LatestServerTick,
    string? FailureCode,
    string? FailureMessage);
