namespace Mmorpg.SimulationWorker.Realtime;

// The one place that orders the steps of the realtime shutdown. A feature registers its step
// under one of these positions, and the shutdown runs the steps from the lowest to the highest.
// The gaps leave room for a new step, which takes a new constant here with its reason.
internal static class RealtimeShutdownOrder
{
    // Commits the arrow shots and quest credits that wait, while the sessions, peers, and
    // entities they name still exist.
    public const int CombatFlush = 100;

    // Saves the place and vitals of every remaining player in one last checkpoint and keeps them
    // for the releases that follow, after the combat flush and while combat state, the entities,
    // and the sessions still hold.
    public const int CharacterStateFlush = 150;

    // Clears combat state once its flush has read it.
    public const int Combat = 200;

    // Clears the peer table, then the interest state, the bindings, and the entities that the
    // peers and combat referred to.
    public const int Peers = 300;

    public const int Presence = 400;

    public const int ConnectionBindings = 500;

    public const int Entities = 600;

    // Forgets the lingering bodies with the entities they were; the flushes saved them like
    // connected players.
    public const int LingeringBodies = 650;

    // Clears the per-character state of every session, after the entities that used it.
    public const int CharacterState = 700;

    // Clears the corpse viewers, the world interaction leases, and the world actors that the
    // cleared sessions and entities held.
    public const int CorpseViewers = 800;

    public const int InteractionLeases = 900;

    public const int WorldActors = 1000;

    // Drops the cached corpse views, which nobody views any more.
    public const int CorpseViews = 1100;

    // Clears the orphan sweep state of the cleared sessions.
    public const int OrphanSweep = 1200;

    // Zeroes the population gauges last, when nothing is left to count.
    public const int PopulationGauges = 1300;
}
