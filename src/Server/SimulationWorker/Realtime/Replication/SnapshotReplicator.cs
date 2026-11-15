using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// Broadcasts movement snapshots: one owner snapshot per player plus interest-filtered
// remote snapshots of players and of the Mobs that moved, within the per-peer and
// aggregate byte budgets. Every entity's remote record is quantized once per snapshot, and
// each recipient's chunks are assembled from those records without allocating. The
// recipient rotation keeps the remote budget fair across peers in registry order. A
// recipient whose packets fail to build or send is disconnected alone, and the others
// still get theirs. The aggregate budget refills on the time provider's clock, the
// system's unless the replication benchmark gives it its own. Loop-confined.
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
    private readonly Dictionary<ulong, OwnerState> ownerStates = [];
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
            records.Reset();
            ownerStates.Clear();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                // Other players see where the character is now, a lingering body without a peer
                // too; an owner gets the state after the last processed input, which its
                // reconciliation replays from.
                records.Add(entity.NetworkEntityId, entity.Movement.State);
                if (!connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out _))
                {
                    continue;
                }

                var confirmed = entity.Movement.ConfirmedState;
                ownerStates.Add(
                    entity.NetworkEntityId,
                    new OwnerState(
                        entity.Movement.LastProcessedInputSequence,
                        RealtimeStateMapper.ToRealtimePlayerState(confirmed),
                        RealtimeStateMapper.ToRealtimePathProgress(confirmed.PathProgress)));
            }

            if (playerBuffer.Count == 0)
            {
                // No bound player has interest to refresh, but despawned world actors
                // must still be pruned, or their tombstones stay until a player joins.
                presence.PruneDespawned();
                return;
            }

            mobs.CopyPendingStatesTo(mobStateBuffer);
            foreach (var mob in mobStateBuffer)
            {
                records.Add(mob.EntityId, mob.State);
            }

            var interestStarted = Stopwatch.GetTimestamp();
            presence.RebuildInterestIndex(playerBuffer);
            presence.RefreshInterests(null);
            performanceMetrics.RecordInterestRefresh(
                Stopwatch.GetElapsedTime(interestStarted));

            unchecked
            {
                snapshotSequence++;
            }

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

            var bytesPerRecipient = Math.Max(
                RealtimeProtocol.OwnerSimulationSnapshotPacketSize,
                config.UdpQuotas.AggregateSnapshotBytesPerSecond
                    / config.SnapshotRateHz
                    / snapshotRecipientBuffer.Count);

            foreach (var recipient in snapshotRecipientBuffer)
            {
                if (!ownerStates.TryGetValue(recipient.ControlledEntityId, out var owner))
                {
                    continue;
                }

                try
                {
                    var ownerPacket = RealtimeProtocol.EncodeOwnerSimulationSnapshot(
                        new RealtimeOwnerSimulationSnapshot(
                            snapshotSequence,
                            clock.ServerTick,
                            owner.LastProcessedInputSequence,
                            owner.State,
                            owner.PathProgress));
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
                try
                {
                    var plan = records.Plan(
                        presence.GetVisibleOrdered(recipient.Context.PeerId),
                        snapshotSequence,
                        bytesPerRecipient,
                        config.SnapshotReplication.OverloadTargetUtilizationBasisPoints);
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

                    // The chunks are built for this recipient alone, so it is a visibility group
                    // of its own.
                    remoteRecipientCount++;
                    for (var packetIndex = 0; packetIndex < plan.PacketCount; packetIndex++)
                    {
                        var packet = records.WriteChunk(
                            packetIndex,
                            snapshotSequence,
                            clock.ServerTick,
                            out var recordCount);
                        encodedPacketCount++;
                        sender.SendUnreliable(recipient.Peer, packet);
                        sentPacketCount++;
                        networkMetrics.RecordSnapshotEntities(recordCount);
                        networkMetrics.RecordSnapshotBytes(packet.Length);
                    }
                }
                catch (Exception exception)
                {
                    isolation.DisconnectPeer(PhaseName, recipient.Peer, recipient.Context, exception);
                }
            }

            snapshotRecipientRotation.Complete(
                snapshotRecipientBuffer.Count,
                lastAdmittedRecipientOffset);
        }
        finally
        {
            performanceMetrics.RecordSnapshotPacketReuse(
                remoteRecipientCount,
                encodedPacketCount,
                sentPacketCount,
                deferredEntityUpdateCount);
            performanceMetrics.RecordSnapshotBroadcast(
                Stopwatch.GetElapsedTime(broadcastStarted));
        }
    }

    private readonly record struct OwnerState(
        uint LastProcessedInputSequence,
        RealtimePlayerState State,
        RealtimeMovementPathProgress PathProgress);
}
