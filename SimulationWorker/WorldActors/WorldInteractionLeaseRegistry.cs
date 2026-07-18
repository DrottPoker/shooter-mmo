using ShooterMmo.GameProtocol;

namespace SimulationWorker.WorldActors;

public sealed record WorldInteractionLease(
    int PeerId,
    Guid InteractionSessionId,
    RealtimeWorldInteractionTargetKind TargetKind,
    ulong TargetEntityId,
    Guid TargetRuntimeId,
    long TargetRevision);

public sealed class WorldInteractionLeaseRegistry
{
    private readonly object syncRoot = new();
    private readonly Dictionary<int, WorldInteractionLease> leasesByPeer = [];

    public bool TryAcquireWorldActor(
        int peerId,
        WorldActorRuntimeState actor,
        out WorldInteractionLease? lease,
        out string code,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(actor);
        lock (syncRoot)
        {
            if (leasesByPeer.ContainsKey(peerId))
            {
                lease = null;
                code = "world_interaction_active";
                message = "Close the active interaction before opening another target.";
                return false;
            }

            lease = new WorldInteractionLease(
                peerId,
                Guid.NewGuid(),
                RealtimeWorldInteractionTargetKind.WorldActor,
                actor.NetworkEntityId,
                actor.RuntimeActorId,
                actor.InteractionRevision);
            leasesByPeer.Add(peerId, lease);
            code = string.Empty;
            message = string.Empty;
            return true;
        }
    }

    public bool TryAcquireCorpse(
        int peerId,
        Guid corpseId,
        out bool created,
        out string code,
        out string message)
    {
        if (peerId < 0 || corpseId == Guid.Empty)
        {
            created = false;
            code = "corpse_interaction_invalid";
            message = "A valid peer and corpse are required.";
            return false;
        }

        lock (syncRoot)
        {
            if (leasesByPeer.TryGetValue(peerId, out var existing))
            {
                if (existing.TargetKind == RealtimeWorldInteractionTargetKind.Corpse
                    && existing.TargetRuntimeId == corpseId)
                {
                    created = false;
                    code = string.Empty;
                    message = string.Empty;
                    return true;
                }

                created = false;
                code = "corpse_interaction_active";
                message = "Close the active world interaction or corpse before opening this corpse.";
                return false;
            }

            leasesByPeer.Add(
                peerId,
                new WorldInteractionLease(
                    peerId,
                    Guid.NewGuid(),
                    RealtimeWorldInteractionTargetKind.Corpse,
                    0,
                    corpseId,
                    0));
            created = true;
            code = string.Empty;
            message = string.Empty;
            return true;
        }
    }

    public bool TryGet(int peerId, out WorldInteractionLease? lease)
    {
        lock (syncRoot)
        {
            return leasesByPeer.TryGetValue(peerId, out lease);
        }
    }

    public bool MatchesWorldActor(
        int peerId,
        Guid interactionSessionId,
        WorldActorRuntimeState actor)
    {
        lock (syncRoot)
        {
            return leasesByPeer.TryGetValue(peerId, out var lease)
                && lease.InteractionSessionId == interactionSessionId
                && lease.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor
                && lease.TargetEntityId == actor.NetworkEntityId
                && lease.TargetRuntimeId == actor.RuntimeActorId;
        }
    }

    public bool ReleaseWorldActor(int peerId, Guid interactionSessionId)
    {
        lock (syncRoot)
        {
            return leasesByPeer.TryGetValue(peerId, out var lease)
                && lease.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor
                && lease.InteractionSessionId == interactionSessionId
                && leasesByPeer.Remove(peerId);
        }
    }

    public bool ReleaseCorpse(int peerId, Guid corpseId)
    {
        lock (syncRoot)
        {
            return leasesByPeer.TryGetValue(peerId, out var lease)
                && lease.TargetKind == RealtimeWorldInteractionTargetKind.Corpse
                && lease.TargetRuntimeId == corpseId
                && leasesByPeer.Remove(peerId);
        }
    }

    public bool RemovePeer(int peerId)
    {
        lock (syncRoot)
        {
            return leasesByPeer.Remove(peerId);
        }
    }

    public IReadOnlyList<WorldInteractionLease> ListWorldActorLeases()
    {
        lock (syncRoot)
        {
            return leasesByPeer.Values
                .Where(lease =>
                    lease.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor)
                .OrderBy(lease => lease.PeerId)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            leasesByPeer.Clear();
        }
    }
}
