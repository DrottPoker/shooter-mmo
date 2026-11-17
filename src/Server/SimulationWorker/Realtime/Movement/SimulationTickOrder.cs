namespace Mmorpg.SimulationWorker.Realtime;

// The one place that orders the phases of a simulation tick. A feature registers its phase
// under one of these positions, and the loop runs the phases from the lowest to the highest.
// The gaps leave room for a new phase, which takes a new constant here with its reason.
//
// A phase that does not work on every tick takes its ticks from SimulationTickCadence, never
// from a modulo of its own, so the snapshot ticks carry the snapshots and no other periodic
// pass: the interest refresh, the changed looks, and the far states run on the tick before each
// snapshot tick, every other pass with a period on a lane off the snapshot ticks and off the
// other lanes, and the character checkpoint on a free tick. RealtimeFeatureRegistrationTests runs the composed
// phases and fails when a periodic phase works on a snapshot tick.
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

    // Ends the lingering bodies whose time is up, whose death committed, or whose session ended,
    // after combat, so a hit of this tick still keeps a body and a death that combat resolved
    // counts, and before the looks and snapshots, so a body that goes leaves its viewers before
    // they get another snapshot of it.
    public const int LingeringBodies = 650;

    // Revalidates open world interactions against this tick's positions, five times a second on
    // its lane.
    public const int InteractionRevalidation = 700;

    // Advances gathering after the revalidation, so an interaction it closed stops gathering
    // in the same tick.
    public const int Gathering = 800;

    // Sends the looks that changed on the tick before each snapshot tick, ahead of the interest
    // refresh, so a spawn it sends carries the look the viewers just got.
    public const int Appearances = 900;

    // Brings the interest index to this tick's positions and refreshes the visibility of the
    // connections whose turn it is, with their spawns and despawns, on the tick before each
    // snapshot tick, after everything that moved, so the snapshot after it carries every entity
    // that came into view.
    public const int Interest = 950;

    // Sends the viewers that see an entity out of detail its newest missed states on the
    // entity's slot, on the tick before each snapshot tick, after the interest refresh, so no
    // state goes to a viewer that just lost the entity and an entity that just came into detail
    // already brought its states.
    public const int FarStates = 960;

    // Sends the movement snapshots on the snapshot ticks, after everything that moved.
    public const int Snapshots = 1000;

    // Sends corpse presence twice a second on its lane.
    public const int CorpsePresence = 1100;

    // Tells the joined players once when GameService stops or starts answering, so their clients
    // show while saving is paused.
    public const int GameServiceAvailability = 1150;

    // Collects the players whose place or vitals changed at the checkpoint interval, on the first
    // free tick once it is due, last, so it sees this tick's movement, damage, deaths, and
    // respawns.
    public const int CharacterCheckpoint = 1200;
}
