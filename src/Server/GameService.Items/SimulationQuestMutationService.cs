using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items;

// Quest actions and kill credits of live characters through the item transaction kernel. The
// worker validated the interaction with the quest's NPC and names that NPC; the kernel checks
// it against the quest.
internal sealed class SimulationQuestMutationService(
    ItemTransactionService transactionService,
    SimulationCharacterStates characterStates)
{
    public async Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
        string authenticatedWorkerId,
        Guid simulationSessionId,
        SimulationQuestOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(authenticatedWorkerId, request.WorkerId, StringComparison.Ordinal))
        {
            return ServiceResult<SimulationItemOperationResponse>.Forbidden(
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
            || !IsOptionalContentIdentifier(request.QuestId)
            || !IsOptionalContentIdentifier(request.QuestNpcActorId)
            || !IsOptionalContentIdentifier(request.ObjectiveId)
            || !IsOptionalContentIdentifier(request.RewardChoiceDefinitionId)
            || !IsOptionalContentIdentifier(request.MobActorId))
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
        var questId = request.QuestId?.Trim() ?? string.Empty;
        var objectiveId = request.ObjectiveId?.Trim() ?? string.Empty;
        var operationKind = request.OperationKind.Trim();
        var named = operationKind switch
        {
            ItemOperationKinds.CreditQuestKill => request.MobActorId is not null && request.MobTags is not null,
            ItemOperationKinds.TalkToQuestNpc or ItemOperationKinds.DeliverQuestItems =>
                questId.Length > 0 && objectiveId.Length > 0,
            _ => questId.Length > 0
        };
        if (!named)
        {
            return Invalid("The simulation quest operation does not name what it acts on.");
        }

        var operation = operationKind switch
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
            ItemOperationKinds.TalkToQuestNpc => transactionService.ExecuteAsync(
                new ItemTransactionRequest<TalkToQuestNpcCommand>(
                    request.OperationId,
                    actor,
                    new TalkToQuestNpcCommand(request.CharacterId, questId, objectiveId)),
                cancellationToken),
            ItemOperationKinds.DeliverQuestItems => transactionService.ExecuteAsync(
                new ItemTransactionRequest<DeliverQuestItemsCommand>(
                    request.OperationId,
                    actor,
                    new DeliverQuestItemsCommand(request.CharacterId, questId, objectiveId)),
                cancellationToken),
            ItemOperationKinds.CreditQuestKill => transactionService.ExecuteAsync(
                new ItemTransactionRequest<CreditQuestKillCommand>(
                    request.OperationId,
                    actor,
                    new CreditQuestKillCommand(
                        request.CharacterId,
                        request.MobActorId!.Trim(),
                        request.MobTags!.Select(tag => tag?.Trim() ?? string.Empty).ToArray())),
                cancellationToken),
            ItemOperationKinds.TurnInQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<TurnInQuestCommand>(
                    request.OperationId,
                    actor,
                    new TurnInQuestCommand(request.CharacterId, questId, request.RewardChoiceDefinitionId?.Trim())),
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
            return SimulationItemRejections.Map<SimulationItemOperationResponse>(result);
        }

        return ServiceResult<SimulationItemOperationResponse>.Ok(SimulationItemOperationResponse.From(
            result,
            await characterStates.ReadAsync(request.CharacterId, cancellationToken)));
    }

    private static bool IsOptionalContentIdentifier(string? value)
    {
        return value is null || Identifiers.IsContentIdentifier(value);
    }

    private static ServiceResult<SimulationItemOperationResponse> Invalid(string message)
    {
        return ServiceResult<SimulationItemOperationResponse>.BadRequest(
            ItemApiErrorCodes.SimulationOperationInvalid,
            message);
    }
}
