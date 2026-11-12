namespace Mmorpg.SimulationWorker.Realtime;

// The one place that orders the phases of a simulation tick. A feature registers its phase
// under one of these positions, and the loop runs the phases from the lowest to the highest.
// The gaps leave room for a new phase, which takes a new constant here with its reason.
internal static class SimulationTickOrder
{
    // Loads the collision chunks around the players, the World spawn, and the moving Mobs
    // before anything moves in them.
    public const int CollisionStreaming = 100;

    // Applies each player's latest committed carry state and then steps its movement, in
    // entity id order, so every later phase sees this tick's player positions.
    public const int Movement = 200;

    // Rebuilds the grid of the moved player positions that the world actor and Mob phases
    // look players up in.
    public const int PlayerGrid = 300;

    // Runs due world actor timers and activity, so an actor that activates or a Mob that
    // respawns takes part in the Mob and combat phases of the same tick.
    public const int WorldActors = 400;

    // Lets Mob brains choose targets and moves the Mobs before combat resolves attacks.
    public const int Mobs = 500;

    // Resolves attacks, impacts, deaths, and respawns on this tick's positions and sends
    // every combat change at its end.
    public const int Combat = 600;

    // Revalidates open world interactions against this tick's positions, five times a second.
    public const int InteractionRevalidation = 700;

    // Advances gathering after the revalidation, so an interaction it closed stops gathering
    // in the same tick.
    public const int Gathering = 800;

    // Sends the looks that changed at the snapshot rate, ahead of the snapshots.
    public const int Appearances = 900;

    // Sends the movement snapshots at the snapshot rate, after everything that moved.
    public const int Snapshots = 1000;

    // Sends corpse presence twice a second.
    public const int CorpsePresence = 1100;

    // Collects the players whose place or vitals changed at the checkpoint interval, last, so it
    // sees this tick's movement, damage, deaths, and respawns.
    public const int CharacterCheckpoint = 1200;
}
