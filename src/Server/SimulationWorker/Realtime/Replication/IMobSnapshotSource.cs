using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

// Where the snapshot broadcast takes the Mobs that moved since the last snapshot: MobSystem in
// the worker, and the moving Mobs of the replication benchmark. Loop-confined.
internal interface IMobSnapshotSource
{
    // Replaces the content of destination with the newest pose of every Mob that moved since the
    // last call, in entity id order.
    void CopyPendingSnapshotsTo(List<RealtimeEntitySnapshot> destination);
}
