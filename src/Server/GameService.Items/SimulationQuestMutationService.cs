using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items;

// Quest actions of live characters through the item transaction kernel. The worker validated
// the interaction with the quest's NPC and names that NPC; the kernel checks it against the
// quest.
internal sealed class SimulationQuestMutationService(
    ItemTransactionService transactionService,
    SimulationCharacterStates characterStates)
{
    public async Task<ServiceResult<SimulationQuestOperationResponse>> ExecuteAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationQuestOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(authenticatedWorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return ServiceResult<SimulationQuestOperationResponse>.Forbidden(
                ItemTransactionErrorCodes.WrongSimulationWorker,
                "The authenticated simulation worker cannot submit another worker's quest operation.");
        }

        if (simulationSessionId == Guid.Empty
            || request.OperationId == Guid.Empty
            || request.AccountId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || !Identifiers.IsServiceIdentifier(request.WorkerId)
            || !Identifiers.IsServiceIdentifier(request.WorkerRuntimeId)
            || !Identifiers.IsServiceIdentifier(request.ShardId)
            || string.IsNullOrWhiteSpace(request.SessionToken)
            || request.SessionToken.Length > 1024
            || string.IsNullOrWhiteSpace(request.OperationKind)
            || !Identifiers.IsContentIdentifier(request.QuestId)
            || (request.QuestNpcActorId is not null && !Identifiers.IsContentIdentifier(request.QuestNpcActorId)))
        {
            return Invalid("The simulation quest operation is incomplete or invalid.");
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId!.Trim(),
            request.WorkerRuntimeId!.Trim(),
            request.ShardId!.Trim(),
            TokenGenerator.HashToken(request.SessionToken!.Trim()),
            new ItemTransactionLiveAccess(false, false, false, QuestNpcActorId: request.QuestNpcActorId?.Trim()));
        var questId = request.QuestId!.Trim();
        var operation = request.OperationKind.Trim() switch
        {
            ItemOperationKinds.AcceptQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AcceptQuestCommand>(
                    request.OperationId,
                    actor,
                    new AcceptQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            ItemOperationKinds.AbandonQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AbandonQuestCommand>(
                    request.OperationId,
                    actor,
                    new AbandonQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            ItemOperationKinds.RegrantQuestItems => transactionService.ExecuteAsync(
                new ItemTransactionRequest<RegrantQuestItemsCommand>(
                    request.OperationId,
                    actor,
                    new RegrantQuestItemsCommand(request.CharacterId, questId)),
                cancellationToken),
            _ => null
        };
        if (operation is null)
        {
            return Invalid("The simulation quest operation kind is not supported.");
        }

        var result = await operation;
        if (!result.Succeeded)
        {
            return SimulationItemRejections.Map<SimulationQuestOperationResponse>(result);
        }

        return ServiceResult<SimulationQuestOperationResponse>.Ok(new SimulationQuestOperationResponse(
            result.OperationId,
            result.OperationKind,
            result.Succeeded,
            result.Error,
            result.CharacterRevisions,
            result.ItemChanges,
            await characterStates.ReadAsync(request.CharacterId, cancellationToken),
            result.QuestLog));
    }

    private static ServiceResult<SimulationQuestOperationResponse> Invalid(string message)
    {
        return ServiceResult<SimulationQuestOperationResponse>.BadRequest(
            ItemApiErrorCodes.SimulationOperationInvalid,
            message);
    }
}
