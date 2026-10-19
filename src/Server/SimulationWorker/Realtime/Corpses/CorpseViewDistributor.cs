using System.Diagnostics.CodeAnalysis;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Owns the last distributed snapshot of every viewed corpse and sends corpse view state
// and closures to viewers. Closing a view always releases the matching interaction
// lease. Loop-confined, like the corpse viewer registry.
internal sealed class CorpseViewDistributor(
    CorpseViewerRegistry corpseViewers,
    WorldInteractionLeaseRegistry? worldInteractionLeases,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    DurableCorpseStore corpseStore,
    LiveMobCorpseStore? liveMobCorpseStore)
{
    private readonly Dictionary<Guid, CorpseViewSnapshotResponse> corpseSnapshots = [];

    public bool TryGetCachedSnapshot(
        Guid corpseId,
        [NotNullWhen(true)] out CorpseViewSnapshotResponse? snapshot)
    {
        return corpseSnapshots.TryGetValue(corpseId, out snapshot);
    }

    public void CacheSnapshot(CorpseViewSnapshotResponse snapshot)
    {
        corpseSnapshots[snapshot.CorpseId] = snapshot;
    }

    public Guid[] GetViewedCorpseIds()
    {
        return corpseSnapshots.Keys.ToArray();
    }

    public void ClearSnapshots()
    {
        corpseSnapshots.Clear();
    }

    public static IReadOnlyList<byte[]> EncodeCorpseStatePackets(
        IReadOnlyList<RealtimeCorpseViewStateChunk> chunks)
    {
        return chunks
            .Select(RealtimeProtocol.EncodeCorpseViewStateChunk)
            .ToArray();
    }

    public void SendCorpseState(IRealtimePeerConnection peer, IReadOnlyList<byte[]> packets)
    {
        foreach (var packet in packets)
        {
            sender.SendControl(peer, packet);
        }
    }

    public void BroadcastCorpseState(
        Guid corpseId,
        IReadOnlyList<byte[]> packets,
        int? excludedPeerId = null)
    {
        foreach (var viewerPeerId in corpseViewers.GetViewers(corpseId))
        {
            if (viewerPeerId == excludedPeerId
                || !peers.TryGetAuthenticatedPeer(viewerPeerId, out _, out var peer))
            {
                continue;
            }

            SendCorpseState(peer, packets);
        }
    }

    public void CloseCorpseForPeer(
        RealtimePeerContext context,
        IRealtimePeerConnection peer,
        Guid corpseId,
        string code,
        string message)
    {
        var closed = corpseViewers.Close(context.PeerId, corpseId);
        worldInteractionLeases?.ReleaseCorpse(context.PeerId, corpseId);
        if (!closed)
        {
            return;
        }

        sender.SendControl(
            peer,
            RealtimeProtocol.EncodeCorpseViewClosed(
                new RealtimeCorpseViewClosed(corpseId, code, message)));
    }

    public void CloseCorpseForAll(
        Guid corpseId,
        string code,
        string message,
        bool removeFromStore)
    {
        foreach (var viewerPeerId in corpseViewers.CloseCorpse(corpseId))
        {
            worldInteractionLeases?.ReleaseCorpse(viewerPeerId, corpseId);
            if (peers.TryGetAuthenticatedPeer(viewerPeerId, out _, out var peer))
            {
                sender.SendControl(
                    peer,
                    RealtimeProtocol.EncodeCorpseViewClosed(
                        new RealtimeCorpseViewClosed(corpseId, code, message)));
            }
        }

        corpseSnapshots.Remove(corpseId);
        if (removeFromStore)
        {
            corpseStore.Remove(corpseId);
            liveMobCorpseStore?.Remove(corpseId);
        }
    }

    // Closes every view of a corpse that is no longer active. A live Mob corpse whose last
    // item was taken closes as emptied, which the client does not present as a failure;
    // any other corpse closes with the given code.
    public void CloseInactiveCorpseForAll(Guid corpseId, string code, string message)
    {
        if (liveMobCorpseStore?.IsEmptied(corpseId) == true)
        {
            code = RealtimeCorpseViewClosureCodes.Emptied;
            message = "The corpse is empty.";
        }

        CloseCorpseForAll(corpseId, code, message, removeFromStore: true);
    }

    public static bool ShouldCloseCorpseForAll(string code)
    {
        return code is RealtimeCorpseViewClosureCodes.Expired
            or RealtimeCorpseViewClosureCodes.Invalidated
            or RealtimeCorpseViewClosureCodes.NotFound;
    }
}
