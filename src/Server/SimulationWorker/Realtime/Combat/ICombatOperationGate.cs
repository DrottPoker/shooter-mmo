using Mmorpg.SimulationWorker.Sessions;

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

    // The operation finished for its session, also when the peer that started it is gone. It is
    // called before the operation's lane is released, so an operation of a reconnected peer that
    // the release starts holds attacks again.
    void ClientOperationFinished(ActiveSimulationSession session);

    long RebaseExpectedCharacterRevision(RealtimePeerContext context, long expectedCharacterRevision);
}
