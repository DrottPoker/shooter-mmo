using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// One batch of spent arrows on its way to GameService. A retry reuses its operation id,
// so a lost answer replays the stored result instead of consuming twice.
internal sealed class AmmunitionBatch(
    Guid operationId,
    Guid itemInstanceId,
    long expectedItemRevision,
    long expectedCharacterRevision,
    int quantity)
{
    public Guid OperationId { get; } = operationId;

    public Guid ItemInstanceId { get; } = itemInstanceId;

    public long ExpectedItemRevision { get; } = expectedItemRevision;

    public long ExpectedCharacterRevision { get; } = expectedCharacterRevision;

    public int Quantity { get; } = quantity;

    public int Attempts { get; set; }

    // While set, the batch waits for a retry after an uncertain outcome.
    public uint? RetryAtTick { get; set; }
}

// A character's death on its way to GameService. The death event id is fixed when the
// character dies, so every retry commits the same death once.
internal sealed class PlayerDeathAttempt(
    Guid deathEventId,
    SimulationDeathKillerRequest? killer,
    float positionX,
    float positionY,
    float positionZ,
    float yawDegrees)
{
    public Guid DeathEventId { get; } = deathEventId;

    public SimulationDeathKillerRequest? Killer { get; } = killer;

    public float PositionX { get; } = positionX;

    public float PositionY { get; } = positionY;

    public float PositionZ { get; } = positionZ;

    public float YawDegrees { get; } = yawDegrees;

    public bool Queued { get; set; }

    public bool InFlight { get; set; }

    public bool Committed { get; set; }

    public int Attempts { get; set; }

    public uint? RetryAtTick { get; set; }
}

// The durable combat work of one simulation session: uncommitted shots, the batch in
// flight, and a death that has not committed. It outlives the peer, so a disconnect or
// leave first commits it and only then releases the session. Loop-confined.
internal sealed class PlayerCombatSession(ActiveSimulationSession session, RealtimePeerContext context)
{
    public ActiveSimulationSession Session { get; set; } = session;

    public RealtimePeerContext Context { get; set; } = context;

    public int PendingShots { get; set; }

    public uint FirstPendingShotTick { get; set; }

    public bool BatchQueued { get; set; }

    public AmmunitionBatch? InFlightBatch { get; set; }

    public PlayerDeathAttempt? Death { get; set; }

    // The peer is gone or leaving; the work left commits before the session ends.
    public bool Detached { get; set; }

    // The committed item state when the peer left, which teardown removes from the store.
    public CharacterItemState? DetachedItemState { get; set; }

    public bool LeaveRequested { get; set; }

    // A client operation that may change an equipment slot is in flight, so attacks wait.
    public bool EquipmentChangePending { get; set; }

    // Item-state revisions that only this session's own batches produced, in order. A
    // client intent computed on one of them is forwarded with the newest.
    public List<long> BatchRevisionChain { get; } = [];

    public bool HasDurableWork => PendingShots > 0
        || BatchQueued
        || InFlightBatch is not null
        || Death is { Committed: false };

    public void RecordBatchRevision(long previousRevision, long committedRevision)
    {
        if (BatchRevisionChain.Count == 0 || BatchRevisionChain[^1] != previousRevision)
        {
            BatchRevisionChain.Clear();
            BatchRevisionChain.Add(previousRevision);
        }

        BatchRevisionChain.Add(committedRevision);
    }
}
