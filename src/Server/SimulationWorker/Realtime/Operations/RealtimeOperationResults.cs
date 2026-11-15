using Mmorpg.GameProtocol;
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

// Follows a completion that waited for the collision chunks of a place: once the completion
// queue passed both, the collision chunk gate no longer holds the place.
internal sealed record PlacementHoldEnded(
    RealtimePeerContext PeerContext,
    long HoldId)
    : RealtimeOperationResult(PeerContext);

// An admin teleport whose destination's collision chunks loaded, to be carried out again.
internal sealed record AdminTeleportReady(
    RealtimePeerContext PeerContext,
    ulong EntityId,
    RealtimeAdminIntent Intent)
    : RealtimeOperationResult(PeerContext);

// QuestLogChange is what the client patches its quest log with after the yield, if anything.
internal sealed record GatheringCycleCompleted(
    RealtimePeerContext PeerContext,
    ActiveSimulationSession Session,
    WorldActorRuntimeState Node,
    long Generation,
    SimulationGatheringRequest Request,
    SimulationGatheringResult Result,
    RealtimeQuestLogChanged? QuestLogChange = null)
    : RealtimeOperationResult(PeerContext);
