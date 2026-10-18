using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Realtime;

// Completed authority operations that the realtime loop finalizes. The record names
// appear in the failure log, so they must stay stable.
internal abstract record RealtimeOperationResult(RealtimePeerContext Context);

internal sealed record JoinCompleted(
    RealtimePeerContext PeerContext,
    SimulationJoinResult<ActiveSimulationSession> Result,
    long EnqueuedTimestamp)
    : RealtimeOperationResult(PeerContext);

internal sealed record LeaveCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    SimulationSessionReleaseResult Result)
    : RealtimeOperationResult(PeerContext);

internal sealed record ItemOperationCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    SimulationItemInteractionResult Result)
    : RealtimeOperationResult(PeerContext);

internal sealed record CorpseOperationCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    SimulationCorpseInteractionResult Result,
    bool RemoveOpenReservationOnFailure)
    : RealtimeOperationResult(PeerContext);

internal sealed record WorldInteractionOperationCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    WorldInteractionAuthorityResult Result)
    : RealtimeOperationResult(PeerContext);

internal sealed record GatheringCycleCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    WorldActorRuntimeState Node,
    long Generation,
    SimulationGatheringRequest Request,
    SimulationGatheringResult Result)
    : RealtimeOperationResult(PeerContext);
