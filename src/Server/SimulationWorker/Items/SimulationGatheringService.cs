using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Items;

// Requests one gathering yield from GameService for a validated gathering cycle. The
// worker resolves the yield and the required tool kind from the node's resource profile;
// GameService checks the equipped tool and places the yield in carried storage.
public sealed class SimulationGatheringService(GameServiceClient gameServiceClient)
{
    public const string OperationKind = "gather_resource";

    public async Task<SimulationGatheringResult> GatherAsync(
        ActiveSimulationSession session,
        SimulationGatheringRequest gathering,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(gathering);
        var profile = gathering.Profile;
        var request = new SimulationItemOperationRequest(
            gathering.OperationId,
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(false, false, false),
            OperationKind,
            0,
            Quantity: profile.YieldQuantity,
            InteractionSessionId: gathering.InteractionSessionId,
            CapabilityId: gathering.CapabilityId,
            DefinitionId: profile.YieldDefinitionId,
            ResourceProfileId: profile.Id,
            ResourceNodeId: gathering.ResourceNodeId,
            RequiredToolKind: profile.RequiredToolKind);
        var result = await gameServiceClient.MutateSimulationItemsAsync(
            session.SimulationSessionId,
            request,
            cancellationToken);
        return result.Succeeded
            ? new SimulationGatheringResult(result.Value, null)
            : new SimulationGatheringResult(null, result.Error);
    }
}

public sealed record SimulationGatheringRequest(
    Guid OperationId,
    Guid InteractionSessionId,
    string CapabilityId,
    string ResourceNodeId,
    WorldActorResourceProfileDefinition Profile);

public sealed record SimulationGatheringResult(
    SimulationItemTransactionResponse? Transaction,
    SimulationWorkerErrorResponse? Error)
{
    public bool Succeeded => Transaction is not null && Error is null;
}
