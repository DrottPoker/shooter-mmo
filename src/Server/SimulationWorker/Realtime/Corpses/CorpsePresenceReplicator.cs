using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;

namespace Mmorpg.SimulationWorker.Realtime;

// Tells every joined peer which corpses are within its discovery radius and closes views
// of corpses that are no longer active. Corpse stores change on other threads, so the
// loop polls them here. A broadcast builds its lists in reusable buffers and sends a peer
// only what differs from what it was sent last, so one that changes nothing allocates
// nothing. Loop-confined.
internal sealed class CorpsePresenceReplicator(
    SimulationWorkerConfig config,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore,
    CorpseRuntimeMetrics? corpseRuntimeMetrics,
    CorpseViewDistributor views,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender)
{
    private readonly List<ICorpseRuntimePresence> activeCorpses = [];
    private readonly HashSet<Guid> activeCorpseIds = [];
    private readonly List<Guid> viewedCorpseIds = [];
    private readonly List<ICorpseRuntimePresence> corpsesInRange = [];
    private readonly Dictionary<Guid, ICorpseRuntimePresence> lootGroups = [];
    private readonly List<ICorpseRuntimePresence> nearbyCorpses = [];
    private uint corpsePresenceSequence;

    // The equipment slots whose items a player corpse shows.
    public IReadOnlyList<string> VisibleEquipmentSlots { get; init; } = [];

    // Sends corpse presence twice a second.
    public void AddTickPhase(SimulationTickPhases phases)
    {
        var intervalTicks = Math.Max(1, config.MovementSimulation.TickRateHz / 2);
        phases.AddPeriodic(
            SimulationTickOrder.CorpsePresence,
            "corpse presence",
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
        activeCorpses.Clear();
        var durableCount = corpseStore.AddActiveTo(activeCorpses);
        var liveCount = liveMobCorpseStore?.AddActiveTo(activeCorpses) ?? 0;
        corpseRuntimeMetrics?.Observe(durableCount, liveCount);
        activeCorpses.Sort(static (left, right) =>
        {
            var created = left.CreatedAt.CompareTo(right.CreatedAt);
            return created != 0 ? created : left.CorpseId.CompareTo(right.CorpseId);
        });
        activeCorpseIds.Clear();
        foreach (var corpse in activeCorpses)
        {
            activeCorpseIds.Add(corpse.CorpseId);
        }

        views.CopyViewedCorpseIdsTo(viewedCorpseIds);
        foreach (var viewedCorpseId in viewedCorpseIds)
        {
            if (!activeCorpseIds.Contains(viewedCorpseId))
            {
                views.CloseInactiveCorpseForAll(
                    viewedCorpseId,
                    RealtimeCorpseViewClosureCodes.Expired,
                    "The corpse expired and is no longer available.");
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
            var characterId = context.Session.CharacterId;
            corpsesInRange.Clear();
            foreach (var corpse in activeCorpses)
            {
                if (IsWithinCorpseDiscoveryRange(corpse, state.PositionX, state.PositionY, state.PositionZ))
                {
                    corpsesInRange.Add(corpse);
                }
            }

            CorpseLootRights.SelectForViewer(corpsesInRange, characterId, lootGroups, nearbyCorpses);
            if (!context.CorpsePresence.Remember(nearbyCorpses, characterId))
            {
                continue;
            }

            foreach (var chunk in CorpseRealtimePacketBuilder.BuildPresence(
                         corpsePresenceSequence,
                         nearbyCorpses,
                         characterId,
                         VisibleEquipmentSlots))
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
}

// What one peer was sent last as its corpse presence: per corpse what the presence shows of it.
// A broadcast sends again only when this changed. Loop-confined.
internal sealed class CorpsePresenceSent
{
    private readonly List<Entry> entries = [];
    private bool sent;

    // Takes the presence the peer gets now and returns whether it differs from the last one;
    // the first one always counts as changed, also when it holds no corpse.
    public bool Remember(IReadOnlyList<ICorpseRuntimePresence> corpses, Guid characterId)
    {
        if (sent && Matches(corpses, characterId))
        {
            return false;
        }

        sent = true;
        entries.Clear();
        foreach (var corpse in corpses)
        {
            entries.Add(Entry.Of(corpse, characterId));
        }

        return true;
    }

    private bool Matches(IReadOnlyList<ICorpseRuntimePresence> corpses, Guid characterId)
    {
        if (corpses.Count != entries.Count)
        {
            return false;
        }

        for (var index = 0; index < corpses.Count; index++)
        {
            if (!entries[index].Equals(Entry.Of(corpses[index], characterId)))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Entry(Guid CorpseId, long Revision, bool IsEmpty, bool HasLook, bool CanLoot)
    {
        public static Entry Of(ICorpseRuntimePresence corpse, Guid characterId) => new(
            corpse.CorpseId,
            corpse.Revision,
            corpse.IsEmpty,
            corpse.Look is not null,
            CorpseLootRights.CanLoot(corpse, characterId));
    }
}
