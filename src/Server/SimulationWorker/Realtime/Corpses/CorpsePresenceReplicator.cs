using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;

namespace Mmorpg.SimulationWorker.Realtime;

// Tells every joined peer which corpses are within its discovery radius and closes views
// of corpses that are no longer active. Corpse stores change on other threads, so the
// loop polls them here. Loop-confined.
internal sealed class CorpsePresenceReplicator(
    SimulationWorkerConfig config,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore,
    CorpseRuntimeMetrics? corpseRuntimeMetrics,
    CorpseViewDistributor views,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender)
{
    private uint corpsePresenceSequence;

    public void Broadcast()
    {
        var durableCorpses = corpseStore.ListActive();
        var liveCorpses = liveMobCorpseStore?.ListActive()
            ?? Array.Empty<LiveMobCorpseState>();
        corpseRuntimeMetrics?.Observe(durableCorpses.Count, liveCorpses.Count);
        var activeCorpses = durableCorpses
            .Cast<ICorpseRuntimePresence>()
            .Concat(liveCorpses)
            .OrderBy(corpse => corpse.CreatedAt)
            .ThenBy(corpse => corpse.CorpseId)
            .ToArray();
        var activeCorpseIds = activeCorpses
            .Select(corpse => corpse.CorpseId)
            .ToHashSet();
        foreach (var viewedCorpseId in views.GetViewedCorpseIds())
        {
            if (!activeCorpseIds.Contains(viewedCorpseId))
            {
                views.CloseCorpseForAll(
                    viewedCorpseId,
                    "corpse_expired",
                    "The corpse expired and is no longer available.",
                    removeFromStore: true);
            }
        }

        unchecked
        {
            corpsePresenceSequence++;
        }

        foreach (var context in peers.Contexts)
        {
            if (context.Session is null
                || !peers.TryGetBoundPlayer(context, out var entity)
                || !peers.TryGetCurrentPeer(context, out var peer))
            {
                continue;
            }

            var state = entity.Movement.State;
            var nearbyCorpses = activeCorpses
                .Where(corpse => IsWithinCorpseDiscoveryRange(
                    corpse,
                    state.PositionX,
                    state.PositionY,
                    state.PositionZ))
                .OrderBy(corpse => corpse.CorpseId)
                .ToArray();
            var signature = CreateCorpsePresenceSignature(nearbyCorpses);
            if (string.Equals(
                    context.CorpsePresenceSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                continue;
            }

            context.CorpsePresenceSignature = signature;
            foreach (var chunk in CorpseRealtimePacketBuilder.BuildPresence(
                         corpsePresenceSequence,
                         nearbyCorpses))
            {
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeCorpsePresenceSnapshotChunk(chunk));
            }
        }
    }

    private bool IsWithinCorpseDiscoveryRange(
        ICorpseRuntimePresence corpse,
        float playerX,
        float playerY,
        float playerZ)
    {
        var deltaX = corpse.PositionX - playerX;
        var deltaY = corpse.PositionY - playerY;
        var deltaZ = corpse.PositionZ - playerZ;
        var radius = config.ItemInteraction.CorpseDiscoveryRadius;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            <= radius * radius;
    }

    private static string CreateCorpsePresenceSignature(
        IReadOnlyList<ICorpseRuntimePresence> corpses)
    {
        return string.Join(
            '|',
            corpses.Select(corpse =>
                $"{corpse.CorpseId:N}:{corpse.Revision}:{corpse.IsEmpty}"));
    }
}
