using System.Diagnostics.CodeAnalysis;
using Mmorpg.SimulationWorker.Entities;

namespace Mmorpg.SimulationWorker.Realtime;

// The peer table of the realtime loop. Iteration follows insertion order, which snapshot
// fairness and broadcast order depend on. Loop-confined.
internal sealed class RealtimePeerRegistry(
    IRealtimePeerDirectory directory,
    ConnectionEntityBindingRegistry connectionBindings,
    SimulationEntityRegistry entityRegistry,
    RealtimeNetworkMetrics networkMetrics)
{
    private readonly Dictionary<int, RealtimePeerContext> peers = [];

    public int Count => peers.Count;

    public Dictionary<int, RealtimePeerContext>.ValueCollection Contexts => peers.Values;

    public void Register(RealtimePeerContext context)
    {
        peers[context.PeerId] = context;
    }

    public bool Remove(int peerId, [NotNullWhen(true)] out RealtimePeerContext? context)
    {
        return peers.Remove(peerId, out context);
    }

    public void Clear()
    {
        peers.Clear();
    }

    public bool TryGetContext(int peerId, [NotNullWhen(true)] out RealtimePeerContext? context)
    {
        return peers.TryGetValue(peerId, out context);
    }

    public bool OwnsPeerId(RealtimePeerContext context)
    {
        return peers.TryGetValue(context.PeerId, out var currentContext)
            && ReferenceEquals(context, currentContext);
    }

    public bool TryGetCurrentPeer(
        RealtimePeerContext context,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = null;
        if (!OwnsPeerId(context))
        {
            return false;
        }

        return directory.TryGetConnectedPeer(context.PeerId, out peer);
    }

    // A peer that still owns its id, holds a session and is connected.
    public bool TryGetAuthenticatedPeer(
        int peerId,
        [NotNullWhen(true)] out RealtimePeerContext? context,
        [NotNullWhen(true)] out IRealtimePeerConnection? peer)
    {
        peer = null;
        return peers.TryGetValue(peerId, out context)
            && context.Session is not null
            && TryGetCurrentPeer(context, out peer);
    }

    public bool TryGetBoundPlayer(
        RealtimePeerContext context,
        [NotNullWhen(true)] out PlayerSimulationEntity? entity)
    {
        if (connectionBindings.TryGetEntityId(context.PeerId, out var entityId)
            && entityRegistry.TryGetPlayer(entityId, out var player))
        {
            entity = player!;
            return true;
        }

        entity = null;
        return false;
    }

    public PlayerSimulationEntity? ResolvePlayerForInteraction(int peerId)
    {
        return peers.TryGetValue(peerId, out var context)
            && context.Session is not null
            && TryGetBoundPlayer(context, out var player)
            ? player
            : null;
    }

    public void RefreshPopulationMetrics()
    {
        var activeRealPlayers = 0;
        var activeSyntheticBots = 0;
        var unauthenticatedPeers = 0;
        foreach (var context in peers.Values)
        {
            if (context.Session is null)
            {
                unauthenticatedPeers++;
            }
            else if (context.Session.IsSyntheticBot)
            {
                activeSyntheticBots++;
            }
            else
            {
                activeRealPlayers++;
            }
        }

        networkMetrics.SetPeerPopulation(
            activeRealPlayers,
            activeSyntheticBots,
            unauthenticatedPeers);
    }
}
