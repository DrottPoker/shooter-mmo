namespace Mmorpg.SimulationWorker.Realtime;

// The fixed-tick server clock. Only the simulation tick loop advances it, and it wraps.
internal sealed class RealtimeSimulationClock
{
    public uint ServerTick { get; private set; }

    public void Advance()
    {
        unchecked
        {
            ServerTick++;
        }
    }
}
