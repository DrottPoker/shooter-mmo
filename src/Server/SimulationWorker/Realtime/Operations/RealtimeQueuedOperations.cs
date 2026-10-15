using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Realtime;

internal abstract record QueuedAuthorityOperation;

internal sealed record QueuedItemOperation(RealtimeItemOperationIntent Intent)
    : QueuedAuthorityOperation;

internal sealed record QueuedCorpseOperation(RealtimeCorpseInteractionIntent Intent)
    : QueuedAuthorityOperation;

internal sealed record QueuedWorldInteractionOperation(
    RealtimeWorldInteractionIntent Intent)
    : QueuedAuthorityOperation;
