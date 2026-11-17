namespace Mmorpg.SimulationWorker.Realtime;

// The states that a viewer can miss of an entity it sees out of detail. It gets the newest of
// each when the entity comes into detail for it, and otherwise on the entity's slot once a
// second.
[Flags]
internal enum MissedStates : byte
{
    None = 0,
    Combatant = 1,
    Look = 2,
    WorldActor = 4
}

internal static class MissedStateKinds
{
    // Every kind on its own, in the order a viewer gets them.
    public static readonly MissedStates[] All = [MissedStates.Combatant, MissedStates.Look, MissedStates.WorldActor];
}
