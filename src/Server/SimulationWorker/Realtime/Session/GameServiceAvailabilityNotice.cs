using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Realtime;

// Tells the clients whether GameService answers, so they show while saving is paused: every
// joined peer once per change of the availability, and a peer that joins while GameService does
// not answer right after its JoinAccepted. A joined peer that hears nothing may take GameService
// as answering. Loop-confined; the tick phase reads the availability without a lock and sends
// one shared packet.
internal sealed class GameServiceAvailabilityNotice
{
    private static readonly byte[] AvailablePacket = RealtimeProtocol.EncodeGameServiceAvailabilityChanged(true);
    private static readonly byte[] UnavailablePacket = RealtimeProtocol.EncodeGameServiceAvailabilityChanged(false);

    private readonly GameServiceAvailability availability;
    private readonly RealtimePeerRegistry peers;
    private readonly RealtimePeerSender sender;
    private readonly ILogger logger;
    private int toldVersion;

    public GameServiceAvailabilityNotice(
        GameServiceAvailability availability,
        RealtimePeerRegistry peers,
        RealtimePeerSender sender,
        ILogger logger)
    {
        this.availability = availability;
        this.peers = peers;
        this.sender = sender;
        this.logger = logger;
        toldVersion = availability.Current.Version;
    }

    public void AddTickPhase(SimulationTickPhases phases)
    {
        phases.AddPeriodic(
            SimulationTickOrder.GameServiceAvailability,
            "game service availability",
            (in SimulationTick tick) => TellChange());
    }

    // A peer that joins while GameService does not answer learns it at once; a peer that joins
    // while it answers hears nothing until the next change.
    public void OnPlayerJoined(RealtimePeerContext context, PlayerSimulationEntity entity)
    {
        if (!availability.IsAvailable && peers.TryGetCurrentPeer(context, out var peer))
        {
            sender.SendControl(peer, UnavailablePacket);
        }
    }

    // False on a tick without a change since the last notice.
    public bool TellChange()
    {
        var (isAvailable, version) = availability.Current;
        if (version == toldVersion)
        {
            return false;
        }

        toldVersion = version;
        var packet = isAvailable ? AvailablePacket : UnavailablePacket;
        var told = 0;
        foreach (var context in peers.Contexts)
        {
            if (context.Session is not null && peers.TryGetCurrentPeer(context, out var peer))
            {
                sender.SendControl(peer, packet);
                told++;
            }
        }

        logger.LogDebug(
            "[SIMULATION] Told {PeerCount} joined peers that GameService {State}.",
            told,
            isAvailable ? "answers again" : "does not answer");
        return true;
    }
}
