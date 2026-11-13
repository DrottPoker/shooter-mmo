using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Quests;
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

// A credited Mob kill on its way to GameService for the character's quests. A retry reuses
// its operation id, so a lost answer replays the stored result instead of counting twice.
internal sealed class QuestKillCredit(Guid operationId, string mobActorId, IReadOnlyList<string> mobTags)
{
    public Guid OperationId { get; } = operationId;

    public string MobActorId { get; } = mobActorId;

    public IReadOnlyList<string> MobTags { get; } = mobTags;

    public int Attempts { get; set; }

    // While set, the credit waits for a retry after an uncertain outcome.
    public uint? RetryAtTick { get; set; }

    public QuestOperation ToOperation()
    {
        return new QuestOperation(
            OperationId,
            QuestOperationKinds.CreditKill,
            null,
            MobActorId: MobActorId,
            MobTags: MobTags,
            ClientIntent: false);
    }
}

// The durable combat work of one simulation session: uncommitted shots, the batch in
// flight, a death that has not committed, and the quest kill credits. It outlives the
// peer: when the connection ends, the character's lingering body commits it without a peer,
// and when the entity goes, the session is released only once it committed. Loop-confined.
internal sealed class PlayerCombatSession(ActiveSimulationSession session, RealtimePeerContext context)
{
    public ActiveSimulationSession Session { get; set; } = session;

    // The peer whose lane the work runs in; after the connection ended, the peer that had it,
    // whose lane flag still tells whether an operation of it runs.
    public RealtimePeerContext Context { get; set; } = context;

    public int PendingShots { get; set; }

    public uint FirstPendingShotTick { get; set; }

    public bool BatchQueued { get; set; }

    public AmmunitionBatch? InFlightBatch { get; set; }

    public PlayerDeathAttempt? Death { get; set; }

    // The connection ended: no peer hears of the work, which the combat phase commits one
    // operation at a time instead of the peer's lane.
    public bool Detached { get; set; }

    // The entity went while work was left: the session is released once the work committed.
    public bool AwaitingRelease { get; set; }

    // The committed item state when the entity went, which teardown removes from the store.
    public CharacterItemState? DetachedItemState { get; set; }

    // A client operation that may change an equipment slot is in flight, so attacks wait.
    public bool EquipmentChangePending { get; set; }

    // Item-state revisions that only this session's own batches produced, in order. A
    // client intent computed on one of them is forwarded with the newest.
    public List<long> BatchRevisionChain { get; } = [];

    // Credited kills that count for the character's quests and wait for their turn in the lane,
    // in kill order.
    public List<QuestKillCredit> QuestCredits { get; } = [];

    public QuestKillCredit? InFlightQuestCredit { get; set; }

    public bool HasQuestCredits => QuestCredits.Count > 0 || InFlightQuestCredit is not null;

    public bool HasDurableWork => PendingShots > 0
        || BatchQueued
        || InFlightBatch is not null
        || Death is { Committed: false }
        || HasQuestCredits;

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
