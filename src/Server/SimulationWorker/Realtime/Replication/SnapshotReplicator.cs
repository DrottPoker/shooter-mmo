using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// Broadcasts movement snapshots: one owner snapshot per player plus interest-filtered
// remote snapshots of players and Mobs. Every entity's remote record is quantized once per
// snapshot, and the scheduler sends each recipient the records that are due for it, nearest
// tier first, within its own byte budget and its share of the aggregate one, assembled
// without allocating. The recipient rotation keeps the aggregate budget fair across peers in
// registry order. A recipient whose packets fail to build or send is disconnected alone, and
// the others still get theirs. The aggregate budget refills on the time provider's clock,
// the system's unless the replication benchmark gives it its own. Loop-confined.
internal sealed class SnapshotReplicator(
    SimulationWorkerConfig config,
    IMobSnapshotSource mobs,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    RealtimeNetworkMetrics networkMetrics,
    RealtimePerformanceMetrics performanceMetrics,
    RealtimeSimulationClock clock,
    RealtimeFailureIsolation isolation,
    TimeProvider? timeProvider = null)
{
    public const string PhaseName = "snapshots";

    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<SnapshotRecipient> snapshotRecipientBuffer = [];
    private readonly List<SnapshotEntityState> mobStateBuffer = [];
    private readonly SnapshotRecords records = new();
    private readonly SnapshotScheduler scheduler = new(config.SnapshotReplication);
    private readonly Dictionary<int, SnapshotRecipientState> recipientStates = [];
    private readonly List<int> staleRecipientStates = [];
    private readonly Dictionary<ulong, OwnerState> ownerStates = [];
    private readonly byte[] ownerPacketBuffer = new byte[RealtimeProtocol.OwnerSimulationSnapshotPacketSize];
    private readonly SnapshotRecipientRotation snapshotRecipientRotation = new();
    private readonly TokenBucket aggregateSnapshotBytes = new(
        config.UdpQuotas.AggregateSnapshotBytesPerSecond,
        config.UdpQuotas.AggregateSnapshotByteBurst,
        timeProvider);
    private uint snapshotSequence;

    // Sends the snapshots at the snapshot rate.
    public void AddTickPhase(SimulationTickPhases phases)
    {
        var intervalTicks = config.MovementSimulation.TickRateHz / config.SnapshotRateHz;
        phases.AddPeriodic(
            SimulationTickOrder.Snapshots,
            PhaseName,
            (in SimulationTick tick) =>
            {
                if (tick.ServerTick % intervalTicks != 0)
                {
                    return false;
                }

                Broadcast();
                return true;
            });
    }

    public void Broadcast()
    {
        var broadcastStarted = Stopwatch.GetTimestamp();
        var sentPacketCount = 0;
        var encodedPacketCount = 0;
        var remoteRecipientCount = 0;
        long deferredEntityUpdateCount = 0;
        try
        {
            var sequence = unchecked(snapshotSequence + 1u);
            records.BeginSnapshot(sequence);
            ownerStates.Clear();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                // Other players see where the character is now, a lingering body without a peer
                // too; an owner gets the state after the last processed input, which its
                // reconciliation replays from.
                records.Report(entity.NetworkEntityId, entity.Movement.State);
                if (!connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out _))
                {
                    continue;
                }

                ownerStates.Add(
                    entity.NetworkEntityId,
                    new OwnerState(entity.Movement.LastProcessedInputSequence, entity.Movement.ConfirmedState));
            }

            if (playerBuffer.Count == 0)
            {
                // No bound player has interest to refresh, but despawned world actors
                // must still be pruned, or their tombstones stay until a player joins.
                presence.PruneDespawned();
                return;
            }

            mobs.CopyStatesTo(mobStateBuffer);
            foreach (var mob in mobStateBuffer)
            {
                records.Report(mob.EntityId, mob.State);
            }

            records.EndReports();
            var interestStarted = Stopwatch.GetTimestamp();
            presence.SynchronizeInterestIndex(playerBuffer);
            presence.RefreshInterests(null);
            performanceMetrics.RecordInterestRefresh(
                Stopwatch.GetElapsedTime(interestStarted));

            snapshotSequence = sequence;

            snapshotRecipientBuffer.Clear();
            foreach (var context in peers.Contexts)
            {
                if (context.Session is null
                    || !connectionBindings.TryGetEntityId(
                        context.PeerId,
                        out var controlledEntityId)
                    || !peers.TryGetCurrentPeer(context, out var peer))
                {
                    continue;
                }

                snapshotRecipientBuffer.Add(new SnapshotRecipient(
                    context,
                    peer,
                    controlledEntityId));
            }

            if (snapshotRecipientBuffer.Count == 0)
            {
                return;
            }

            // A recipient's own budget, or its share of the aggregate one when that is smaller.
            var bytesPerRecipient = Math.Max(
                RealtimeProtocol.OwnerSimulationSnapshotPacketSize,
                Math.Min(
                    config.SnapshotReplication.PlayerBytesPerSecond / config.SnapshotRateHz,
                    config.UdpQuotas.AggregateSnapshotBytesPerSecond
                        / config.SnapshotRateHz
                        / snapshotRecipientBuffer.Count));

            foreach (var recipient in snapshotRecipientBuffer)
            {
                if (!ownerStates.TryGetValue(recipient.ControlledEntityId, out var owner))
                {
                    continue;
                }

                try
                {
                    var ownerPacket = EncodeOwnerSnapshot(owner);
                    encodedPacketCount++;
                    if (!aggregateSnapshotBytes.TryConsume(ownerPacket.Length))
                    {
                        networkMetrics.RecordSnapshotBackpressureDropped(1);
                        continue;
                    }

                    if (!recipient.Context.Quota.TryConsumeSnapshot(ownerPacket.Length))
                    {
                        networkMetrics.RecordSnapshotDropped(1);
                        continue;
                    }

                    sender.SendUnreliable(recipient.Peer, ownerPacket);
                    sentPacketCount++;
                    networkMetrics.RecordSnapshotEntities(1);
                    networkMetrics.RecordSnapshotBytes(ownerPacket.Length);
                }
                catch (Exception exception)
                {
                    isolation.DisconnectPeer(PhaseName, recipient.Peer, recipient.Context, exception);
                }
            }

            var recipientStart = snapshotRecipientRotation.Begin(
                snapshotRecipientBuffer.Count);
            var lastAdmittedRecipientOffset = -1;
            for (var recipientIndex = 0;
                recipientIndex < snapshotRecipientBuffer.Count;
                recipientIndex++)
            {
                var recipient = snapshotRecipientBuffer[
                    (recipientStart + recipientIndex) % snapshotRecipientBuffer.Count];
                var sent = false;
                try
                {
                    presence.GetVisibleSpans(recipient.Context.PeerId, out var orderedVisible, out var orderedNear, out var visibleVersion);
                    var plan = scheduler.Plan(
                        records,
                        RecipientStateOf(recipient),
                        orderedVisible,
                        orderedNear,
                        bytesPerRecipient,
                        visibleVersion);
                    deferredEntityUpdateCount += plan.DeferredCount;
                    if (plan.PacketCount == 0)
                    {
                        continue;
                    }

                    if (!aggregateSnapshotBytes.TryConsume(plan.TotalBytes))
                    {
                        networkMetrics.RecordSnapshotBackpressureDropped(plan.PacketCount);
                        continue;
                    }

                    lastAdmittedRecipientOffset = recipientIndex;

                    if (!recipient.Context.Quota.TryConsumeSnapshot(plan.TotalBytes))
                    {
                        networkMetrics.RecordSnapshotDropped(plan.PacketCount);
                        continue;
                    }

                    remoteRecipientCount++;
                    for (var packetIndex = 0; packetIndex < plan.PacketCount; packetIndex++)
                    {
                        var packet = scheduler.WriteChunk(
                            packetIndex,
                            clock.ServerTick,
                            out var recordCount);
                        encodedPacketCount++;
                        sender.SendUnreliable(recipient.Peer, packet);
                        sentPacketCount++;
                        networkMetrics.RecordSnapshotEntities(recordCount);
                        networkMetrics.RecordSnapshotBytes(packet.Length);
                    }

                    sent = true;
                }
                catch (Exception exception)
                {
                    isolation.DisconnectPeer(PhaseName, recipient.Peer, recipient.Context, exception);
                }
                finally
                {
                    scheduler.Complete(sent);
                }
            }

            snapshotRecipientRotation.Complete(
                snapshotRecipientBuffer.Count,
                lastAdmittedRecipientOffset);
            ForgetStaleRecipients();
        }
        finally
        {
            performanceMetrics.RecordSnapshotSends(
                remoteRecipientCount,
                encodedPacketCount,
                sentPacketCount,
                deferredEntityUpdateCount);
            performanceMetrics.RecordSnapshotBroadcast(
                Stopwatch.GetElapsedTime(broadcastStarted));
        }
    }

    // What the recipient got so far, or a new state for another connection or character.
    private SnapshotRecipientState RecipientStateOf(SnapshotRecipient recipient)
    {
        var peerId = recipient.Context.PeerId;
        if (!recipientStates.TryGetValue(peerId, out var state)
            || !ReferenceEquals(state.Context, recipient.Context)
            || state.ControlledEntityId != recipient.ControlledEntityId)
        {
            state = new SnapshotRecipientState(recipient.Context, recipient.ControlledEntityId);
            recipientStates[peerId] = state;
        }

        return state;
    }

    // Drops the states of connections that this snapshot had no recipient for.
    private void ForgetStaleRecipients()
    {
        if (recipientStates.Count <= snapshotRecipientBuffer.Count)
        {
            return;
        }

        staleRecipientStates.Clear();
        foreach (var pair in recipientStates)
        {
            if (pair.Value.LastSequence != snapshotSequence)
            {
                staleRecipientStates.Add(pair.Key);
            }
        }

        foreach (var peerId in staleRecipientStates)
        {
            recipientStates.Remove(peerId);
        }
    }

    // The owner snapshot in the reusable owner packet buffer, which the transport copies. It throws
    // for a state that is not finite, as the protocol's validation does.
    private ReadOnlySpan<byte> EncodeOwnerSnapshot(in OwnerState owner)
    {
        var state = owner.State;
        var progress = state.PathProgress;
        var length = RealtimeProtocol.EncodeOwnerSimulationSnapshot(
            ownerPacketBuffer,
            snapshotSequence,
            clock.ServerTick,
            owner.LastProcessedInputSequence,
            state.PositionX,
            state.PositionY,
            state.PositionZ,
            state.VelocityX,
            state.VelocityY,
            state.VelocityZ,
            state.YawDegrees,
            state.IsGrounded,
            progress.PathId,
            (byte)progress.NextWaypointIndex,
            (byte)progress.BlockedTicks);
        return ownerPacketBuffer.AsSpan(0, length);
    }

    // The state after the owner's last processed input, which its reconciliation replays from.
    private readonly record struct OwnerState(uint LastProcessedInputSequence, PlayerMovementState State);
}
