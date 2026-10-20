using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// Broadcasts movement snapshots: one owner snapshot per player plus interest-filtered
// remote snapshots of players and of the Mobs that moved, within the per-peer and
// aggregate byte budgets. The recipient rotation keeps the remote budget fair across
// peers in registry order. Loop-confined.
internal sealed class SnapshotReplicator(
    SimulationWorkerConfig config,
    MobSystem mobs,
    SimulationEntityRegistry entityRegistry,
    ConnectionEntityBindingRegistry connectionBindings,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    EntityPresenceReplicator presence,
    RealtimeNetworkMetrics networkMetrics,
    RealtimePerformanceMetrics performanceMetrics,
    RealtimeSimulationClock clock)
{
    private readonly List<PlayerSimulationEntity> playerBuffer = [];
    private readonly List<SnapshotRecipient> snapshotRecipientBuffer = [];
    private readonly List<RealtimeEntitySnapshot> mobSnapshotBuffer = [];
    private readonly SnapshotPacketCache snapshotPacketCache = new();
    private readonly Dictionary<ulong, OwnerState> ownerStates = [];
    private readonly SnapshotRecipientRotation snapshotRecipientRotation = new();
    private readonly TokenBucket aggregateSnapshotBytes = new(
        config.UdpQuotas.AggregateSnapshotBytesPerSecond,
        config.UdpQuotas.AggregateSnapshotByteBurst);
    private uint snapshotSequence;

    public void Broadcast()
    {
        var broadcastStarted = Stopwatch.GetTimestamp();
        var sentPacketCount = 0;
        var encodedOwnerPacketCount = 0;
        long deferredEntityUpdateCount = 0;
        try
        {
            snapshotPacketCache.Reset();
            ownerStates.Clear();
            entityRegistry.CopyPlayersTo(playerBuffer);
            foreach (var entity in playerBuffer)
            {
                if (!connectionBindings.TryGetConnectionId(entity.NetworkEntityId, out _))
                {
                    continue;
                }

                // Other players see where the character is now; its owner gets the state after
                // the last processed input, which its reconciliation replays from.
                snapshotPacketCache.Add(new RealtimeEntitySnapshot(
                    entity.NetworkEntityId,
                    entity.Movement.LastProcessedInputSequence,
                    RealtimeStateMapper.ToRealtimePlayerState(entity.Movement.State)));
                var confirmed = entity.Movement.ConfirmedState;
                ownerStates.Add(
                    entity.NetworkEntityId,
                    new OwnerState(
                        entity.Movement.LastProcessedInputSequence,
                        RealtimeStateMapper.ToRealtimePlayerState(confirmed),
                        RealtimeStateMapper.ToRealtimePathProgress(confirmed.PathProgress)));
            }

            if (snapshotPacketCache.EntitySnapshotCount == 0)
            {
                // No bound player has interest to refresh, but despawned world actors
                // must still be pruned, or their tombstones stay until a player joins.
                presence.PruneDespawned();
                return;
            }

            mobs.CopyPendingSnapshotsTo(mobSnapshotBuffer);
            foreach (var mobSnapshot in mobSnapshotBuffer)
            {
                snapshotPacketCache.Add(mobSnapshot);
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

                var ownerPacket = RealtimeProtocol.EncodeOwnerSimulationSnapshot(
                    new RealtimeOwnerSimulationSnapshot(
                        snapshotSequence,
                        clock.ServerTick,
                        owner.LastProcessedInputSequence,
                        owner.State,
                        owner.PathProgress));
                encodedOwnerPacketCount++;
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

            var recipientStart = snapshotRecipientRotation.Begin(
                snapshotRecipientBuffer.Count);
            var lastAdmittedRecipientOffset = -1;
            for (var recipientIndex = 0;
                recipientIndex < snapshotRecipientBuffer.Count;
                recipientIndex++)
            {
                var recipient = snapshotRecipientBuffer[
                    (recipientStart + recipientIndex) % snapshotRecipientBuffer.Count];
                var packetBatch = snapshotPacketCache.GetOrCreate(
                    presence.GetVisibleOrdered(recipient.Context.PeerId),
                    snapshotSequence,
                    clock.ServerTick,
                    bytesPerRecipient,
                    config.SnapshotReplication.OverloadTargetUtilizationBasisPoints);
                deferredEntityUpdateCount += packetBatch.DeferredEntityCount;
                if (packetBatch.Packets.Count == 0)
                {
                    continue;
                }

                if (!aggregateSnapshotBytes.TryConsume(packetBatch.TotalBytes))
                {
                    networkMetrics.RecordSnapshotBackpressureDropped(
                        packetBatch.Packets.Count);
                    continue;
                }

                lastAdmittedRecipientOffset = recipientIndex;

                if (!recipient.Context.Quota.TryConsumeSnapshot(packetBatch.TotalBytes))
                {
                    networkMetrics.RecordSnapshotDropped(packetBatch.Packets.Count);
                    continue;
                }

                for (var packetIndex = 0; packetIndex < packetBatch.Packets.Count; packetIndex++)
                {
                    var packet = packetBatch.Packets[packetIndex];
                    sender.SendUnreliable(recipient.Peer, packet);
                    sentPacketCount++;
                    networkMetrics.RecordSnapshotEntities(
                        packetBatch.EntityCounts[packetIndex]);
                    networkMetrics.RecordSnapshotBytes(packet.Length);
                }
            }


            snapshotRecipientRotation.Complete(
                snapshotRecipientBuffer.Count,
                lastAdmittedRecipientOffset);
        }
        finally
        {
            performanceMetrics.RecordSnapshotPacketReuse(
                snapshotPacketCache.VisibilityGroupCount,
                snapshotPacketCache.EncodedPacketCount + encodedOwnerPacketCount,
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
