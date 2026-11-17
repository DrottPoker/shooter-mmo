namespace Mmorpg.SimulationWorker.Realtime;

// Sends the viewers that see an entity out of detail its newest states once a second: every
// entity has a slot in the second, its id modulo the snapshots per second, and on the tick before
// the snapshot of its slot every viewer that missed a combatant state, look, or world actor state
// of it gets the newest one, encoded once per entity. A state that is held back, such as the
// state of a walking Mob, stays missed until a later slot. It runs after the interest refresh,
// so a viewer that left the entity's view gets nothing of it. A peer that a state fails to reach
// is disconnected alone. Loop-confined.
internal sealed class FarStateRefresher(
    EntityPresenceReplicator presence,
    RealtimePeerRegistry peers,
    RealtimePeerSender sender,
    RealtimeFailureIsolation isolation,
    int snapshotRateHz)
{
    public const string PhaseName = "far states";

    private readonly uint slotCount = (uint)Math.Max(1, snapshotRateHz);
    private readonly List<SimulationInterestMissed> missed = [];

    // The newest states of the entity whose viewers get them now, per kind as MissedStateKinds
    // orders them: encoded once, or held back.
    private readonly byte[]?[] packets = new byte[]?[MissedStateKinds.All.Length];
    private readonly bool[] encoded = new bool[MissedStateKinds.All.Length];
    private readonly bool[] heldBack = new bool[MissedStateKinds.All.Length];

    public void AddTickPhase(SimulationTickPhases phases, SimulationTickCadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        var snapshotInterval = (uint)cadence.SnapshotIntervalTicks;
        phases.AddPeriodic(
            SimulationTickOrder.FarStates,
            PhaseName,
            (in SimulationTick tick) =>
            {
                if (!cadence.IsBeforeSnapshotTick(tick.ServerTick))
                {
                    return false;
                }

                Send(unchecked(tick.ServerTick + 1u) / snapshotInterval % slotCount);
                return true;
            });
    }

    // Sends the newest missed states of the entities on the slot, those whose id modulo the
    // snapshots per second is slotPhase.
    public void Send(uint slotPhase)
    {
        presence.TakeMissedOnSlot(slotPhase, slotCount, missed);
        ulong entityId = 0;
        foreach (var entry in missed)
        {
            if (entry.EntityId != entityId)
            {
                entityId = entry.EntityId;
                Array.Clear(packets);
                Array.Clear(encoded);
                Array.Clear(heldBack);
            }

            if (!peers.TryGetAuthenticatedPeer(entry.ConnectionId, out var context, out var peer))
            {
                continue;
            }

            var kept = MissedStates.None;
            try
            {
                for (var index = 0; index < MissedStateKinds.All.Length; index++)
                {
                    var kind = MissedStateKinds.All[index];
                    if ((entry.States & kind) == 0)
                    {
                        continue;
                    }

                    if (!encoded[index])
                    {
                        packets[index] = presence.EncodeNewestState(entityId, kind, out heldBack[index]);
                        encoded[index] = true;
                    }

                    if (heldBack[index])
                    {
                        kept |= kind;
                    }
                    else if (packets[index] is { } packet)
                    {
                        sender.SendControl(peer, packet);
                    }
                }
            }
            catch (Exception exception)
            {
                isolation.DisconnectPeer(PhaseName, peer, context, exception);
                continue;
            }

            presence.MarkMissed(entry.ConnectionId, entityId, kept);
        }
    }
}
