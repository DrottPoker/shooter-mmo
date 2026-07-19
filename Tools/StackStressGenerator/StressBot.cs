using System.Diagnostics;
using ShooterMmo.GameProtocol;
using ShooterMmo.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = ShooterMmo.Tools.SimulationBotClient.SimulationBotClient;

namespace ShooterMmo.Tools.StackStressGenerator;

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
    private const string InventoryDefinitionId = "medical.field_dressing";
    private const string LootDefinitionId = "material.iron_ore";

    private static readonly HashSet<string> ExpectedLootConflictCodes = new(
        StringComparer.Ordinal)
    {
        "item_already_looted",
        "item_quantity_changed",
        "item_state_conflict",
        "item_slot_occupied",
        "item_stack_limit_exceeded",
        "corpse_view_not_open"
    };

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
            !result.Succeeded && ExpectedLootConflictCodes.Contains(code!));

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

        var owned = inventory.FindItem(InventoryDefinitionId);
        if (owned is null)
        {
            inventoryRefreshRequested = true;
            return;
        }

        var destinationId = owned.ContainerId == inventory.PermanentInventoryId
            ? inventory.SecureContainerId
            : inventory.PermanentInventoryId;
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

        var ownedLoot = inventory.FindItem(LootDefinitionId);
        if (ownedLoot is not null)
        {
            var target = corpseView.FindLootStack(
                LootDefinitionId,
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

        var loot = corpseView.FindLootStack(LootDefinitionId, requireAvailableCapacity: false);
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

    private sealed class StressBotInputSource(
        int botIndex,
        int seed,
        StressWorkloadProfile workload) : ISimulationBotInputSource
    {
        public RealtimeMovementInput CreateInput(
            uint inputSequence,
            uint clientTick,
            RealtimeJoinAccepted joinedSession)
        {
            if (workload == StressWorkloadProfile.LootHotspot)
            {
                return new RealtimeMovementInput(
                    inputSequence,
                    clientTick,
                    0,
                    0,
                    (botIndex * 17f) % 360f,
                    RealtimeMovementButtons.Aim);
            }

            if (workload == StressWorkloadProfile.MixedGameplay)
            {
                var phaseTicks = Math.Max(1u, joinedSession.MovementSettings.TickRateHz / 3u);
                var phase = (clientTick / phaseTicks + (uint)botIndex) % 4u;
                var mixedMoveY = phase switch
                {
                    0 => 0.5f,
                    2 => -0.5f,
                    _ => 0f
                };
                return new RealtimeMovementInput(
                    inputSequence,
                    clientTick,
                    0,
                    mixedMoveY,
                    (botIndex * 17f + clientTick) % 360f,
                    RealtimeMovementButtons.Aim);
            }

            var ticksPerPhase = Math.Max(1u, joinedSession.MovementSettings.TickRateHz * 2u);
            var movementPhase = (int)(((clientTick / ticksPerPhase)
                + (uint)(botIndex + seed)) % 4u);
            var (moveX, moveY) = movementPhase switch
            {
                0 => (0f, 1f),
                1 => (1f, 0f),
                2 => (0f, -1f),
                _ => (-1f, 0f)
            };
            var buttons = movementPhase == 3
                ? RealtimeMovementButtons.Aim
                : RealtimeMovementButtons.Sprint;
            var jumpInterval = Math.Max(1u, joinedSession.MovementSettings.TickRateHz * 5u);
            if (movementPhase != 3
                && clientTick % jumpInterval == (uint)(botIndex % jumpInterval))
            {
                buttons |= RealtimeMovementButtons.Jump;
            }

            var yaw = (botIndex * 17f + clientTick * 1.5f) % 360f;
            return new RealtimeMovementInput(
                inputSequence,
                clientTick,
                moveX,
                moveY,
                yaw,
                buttons);
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
