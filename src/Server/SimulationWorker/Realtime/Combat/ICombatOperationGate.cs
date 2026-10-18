namespace Mmorpg.SimulationWorker.Realtime;

// What combat asks of every client item, corpse, and world interaction operation: a dead
// character starts none, spent arrows commit first, an operation that may change an
// equipment slot holds attacks until it finishes, and an expected item-state revision
// that only the worker's own arrow batches advanced is forwarded with the newest one.
internal interface ICombatOperationGate
{
    bool IsCharacterDead(RealtimePeerContext context);

    void BeforeClientOperation(RealtimePeerContext context);

    void ClientOperationStarting(RealtimePeerContext context, bool mayChangeEquipment);

    void ClientOperationFinished(RealtimePeerContext context);

    long RebaseExpectedCharacterRevision(RealtimePeerContext context, long expectedCharacterRevision);
}

// Hears every player entity that teardown removes, with the session it belonged to.
internal interface IPlayerEntityRemovalObserver
{
    void OnPlayerEntityRemoved(RealtimePeerContext context, Sessions.ActiveSimulationSession session, ulong entityId);
}
