namespace SimulationWorker.Corpses;

public sealed class CorpseViewerRegistry
{
    private readonly Dictionary<int, Guid> corpseByPeer = [];
    private readonly Dictionary<Guid, HashSet<int>> peersByCorpse = [];

    public bool TryOpen(int peerId, Guid corpseId, out string code, out string message)
    {
        if (peerId < 0 || corpseId == Guid.Empty)
        {
            code = "corpse_interaction_invalid";
            message = "A valid peer and corpse are required.";
            return false;
        }

        if (corpseByPeer.TryGetValue(peerId, out var activeCorpseId))
        {
            if (activeCorpseId == corpseId)
            {
                code = string.Empty;
                message = string.Empty;
                return true;
            }

            code = "corpse_interaction_active";
            message = "Close the active corpse before opening another corpse.";
            return false;
        }

        corpseByPeer.Add(peerId, corpseId);
        if (!peersByCorpse.TryGetValue(corpseId, out var viewers))
        {
            viewers = [];
            peersByCorpse.Add(corpseId, viewers);
        }

        viewers.Add(peerId);
        code = string.Empty;
        message = string.Empty;
        return true;
    }

    public bool IsViewing(int peerId, Guid corpseId)
    {
        return corpseByPeer.TryGetValue(peerId, out var activeCorpseId)
            && activeCorpseId == corpseId;
    }

    public bool TryGetActiveCorpse(int peerId, out Guid corpseId)
    {
        return corpseByPeer.TryGetValue(peerId, out corpseId);
    }

    public bool Close(int peerId, Guid corpseId)
    {
        return corpseByPeer.TryGetValue(peerId, out var activeCorpseId)
            && activeCorpseId == corpseId
            && RemovePeer(peerId);
    }

    public bool RemovePeer(int peerId)
    {
        if (!corpseByPeer.Remove(peerId, out var corpseId))
        {
            return false;
        }

        var viewers = peersByCorpse[corpseId];
        viewers.Remove(peerId);
        if (viewers.Count == 0)
        {
            peersByCorpse.Remove(corpseId);
        }

        return true;
    }

    public IReadOnlyList<int> GetViewers(Guid corpseId)
    {
        return peersByCorpse.TryGetValue(corpseId, out var viewers)
            ? viewers.Order().ToArray()
            : [];
    }

    public IReadOnlyList<int> CloseCorpse(Guid corpseId)
    {
        if (!peersByCorpse.Remove(corpseId, out var viewers))
        {
            return [];
        }

        foreach (var peerId in viewers)
        {
            corpseByPeer.Remove(peerId);
        }

        return viewers.Order().ToArray();
    }

    public void Clear()
    {
        corpseByPeer.Clear();
        peersByCorpse.Clear();
    }
}
