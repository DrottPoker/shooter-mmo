namespace Mmorpg.SimulationWorker.Realtime;

// Where the snapshot broadcast takes the Mobs: MobSystem in the worker, and the Mobs of the
// replication benchmark. Loop-confined.
internal interface IMobSnapshotSource
{
    // Replaces the content of destination with the newest pose of every living Mob that walks,
    // in entity id order. The snapshot sends a Mob's record by the same rules as a player's.
    void CopyStatesTo(List<SnapshotEntityState> destination);
}
