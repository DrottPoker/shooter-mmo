using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// What the phases of one tick share: the server tick, the players of the tick, and the grid of
// their positions, which the player grid phase rebuilds.
internal readonly struct SimulationTick(
    uint serverTick,
    SimulationTickPlayers players,
    PlayerSpatialGrid playerGrid)
{
    public uint ServerTick { get; } = serverTick;

    public SimulationTickPlayers Players { get; } = players;

    public PlayerSpatialGrid PlayerGrid { get; } = playerGrid;
}

// The players of one tick in entity id order, from the buffer the loop fills once per tick. A
// read-only view whose enumeration allocates nothing.
internal readonly struct SimulationTickPlayers
{
    private readonly List<PlayerSimulationEntity> players;

    public SimulationTickPlayers(List<PlayerSimulationEntity> players)
    {
        this.players = players;
    }

    public int Count => players.Count;

    public PlayerSimulationEntity this[int index] => players[index];

    public List<PlayerSimulationEntity>.Enumerator GetEnumerator()
    {
        return players.GetEnumerator();
    }

    // For an API that takes a list; it must not keep it past the tick.
    public IReadOnlyList<PlayerSimulationEntity> AsReadOnlyList()
    {
        return players;
    }
}

internal delegate void SimulationTickPhaseAction(in SimulationTick tick);

// A phase that works only on some ticks. It returns false for a tick on which it was not due,
// so that tick counts neither as a failed nor as a successful run of the phase.
internal delegate bool SimulationPeriodicTickPhaseAction(in SimulationTick tick);

internal sealed record SimulationTickPhase(int Order, string Name, SimulationPeriodicTickPhaseAction Run);

// The phases of a simulation tick that features register during composition, each under its
// position in SimulationTickOrder. Loop-confined once composed.
internal sealed class SimulationTickPhases
{
    private readonly List<SimulationTickPhase> phases = [];

    // A phase that does its work on every tick.
    public void Add(int order, string name, SimulationTickPhaseAction run)
    {
        ArgumentNullException.ThrowIfNull(run);
        AddPeriodic(
            order,
            name,
            (in SimulationTick tick) =>
            {
                run(in tick);
                return true;
            });
    }

    // A phase that says for each tick whether it did its work, such as one that broadcasts
    // every second tick. Only a run that did work resets the phase's count of failed runs.
    public void AddPeriodic(int order, string name, SimulationPeriodicTickPhaseAction run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        foreach (var phase in phases)
        {
            if (phase.Order == order)
            {
                throw new InvalidOperationException(
                    $"The tick phases '{phase.Name}' and '{name}' both take order {order}.");
            }
        }

        phases.Add(new SimulationTickPhase(order, name, run));
    }

    // The phases in the order the loop runs them.
    public SimulationTickPhase[] InTickOrder()
    {
        return [.. phases.OrderBy(phase => phase.Order)];
    }
}
