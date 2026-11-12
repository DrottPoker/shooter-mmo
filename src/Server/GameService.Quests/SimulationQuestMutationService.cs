using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.GameService.Quests;

// Quest actions and kill credits of live characters through the item transaction kernel. The
// worker validated the interaction with the quest's NPC and names that NPC; the kernel checks
// it against the quest. The Admin menu's quest tools run only in the Development environment.
internal sealed class SimulationQuestMutationService(
    IItemTransactions transactionService,
    SimulationCharacterStates characterStates,
    QuestsModuleOptions options,
    IQuestDefinitions questDefinitions)
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
            || !IsOptionalContentIdentifier(request.MobActorId)
            || request.MobTags is { Count: > WorldActorCompiler.MaximumTagsPerActor }
            || request.MobTags?.Any(tag => !Identifiers.IsContentIdentifier(tag)) == true)
        {
            return Invalid("The simulation quest operation is incomplete or invalid.");
        }

        if (!QuestCatalogFence.Admits(questDefinitions, request.QuestCatalogRevision))
        {
            return ServiceResult<SimulationItemOperationResponse>.Conflict(
                QuestCatalogFence.MismatchCode,
                QuestCatalogFence.MismatchMessage);
        }

        var actor = ItemTransactionActor.ForSimulationWorker(
            request.AccountId,
            request.CharacterId,
            simulationSessionId,
            request.WorkerId!.Trim(),
            request.WorkerRuntimeId!.Trim(),
            request.ShardId!.Trim(),
            TokenGenerator.HashToken(request.SessionToken!.Trim()),
            QuestNpcAccess.Claim(ItemTransactionLiveAccess.None, request.QuestNpcActorId?.Trim()));
        var questId = request.QuestId?.Trim() ?? string.Empty;
        var objectiveId = request.ObjectiveId?.Trim() ?? string.Empty;
        var operationKind = request.OperationKind.Trim();
        if (IsDevelopmentTool(operationKind) && !options.DevelopmentQuestTools)
        {
            return ServiceResult<SimulationItemOperationResponse>.Forbidden(
                QuestTransactionErrorCodes.DevelopmentToolsDisabled,
                "Only a Development GameService runs the Admin menu's quest tools.");
        }

        var named = operationKind switch
        {
            QuestOperationKinds.CreditQuestKill => request.MobActorId is not null && request.MobTags is not null,
            QuestOperationKinds.TalkToQuestNpc
                or QuestOperationKinds.DeliverQuestItems
                or QuestOperationKinds.AdminCompleteQuestObjective =>
                questId.Length > 0 && objectiveId.Length > 0,
            QuestOperationKinds.AdminResetQuests => questId.Length == 0,
            _ => questId.Length > 0
        };
        if (!named)
        {
            return Invalid("The simulation quest operation does not name what it acts on.");
        }

        var operation = operationKind switch
        {
            QuestOperationKinds.AcceptQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AcceptQuestCommand>(
                    request.OperationId,
                    actor,
                    new AcceptQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AbandonQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AbandonQuestCommand>(
                    request.OperationId,
                    actor,
                    new AbandonQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.RegrantQuestItems => transactionService.ExecuteAsync(
                new ItemTransactionRequest<RegrantQuestItemsCommand>(
                    request.OperationId,
                    actor,
                    new RegrantQuestItemsCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.TalkToQuestNpc => transactionService.ExecuteAsync(
                new ItemTransactionRequest<TalkToQuestNpcCommand>(
                    request.OperationId,
                    actor,
                    new TalkToQuestNpcCommand(request.CharacterId, questId, objectiveId)),
                cancellationToken),
            QuestOperationKinds.DeliverQuestItems => transactionService.ExecuteAsync(
                new ItemTransactionRequest<DeliverQuestItemsCommand>(
                    request.OperationId,
                    actor,
                    new DeliverQuestItemsCommand(request.CharacterId, questId, objectiveId)),
                cancellationToken),
            QuestOperationKinds.CreditQuestKill => transactionService.ExecuteAsync(
                new ItemTransactionRequest<CreditQuestKillCommand>(
                    request.OperationId,
                    actor,
                    new CreditQuestKillCommand(
                        request.CharacterId,
                        request.MobActorId!.Trim(),
                        request.MobTags!.Select(tag => tag?.Trim() ?? string.Empty).ToArray())),
                cancellationToken),
            QuestOperationKinds.TurnInQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<TurnInQuestCommand>(
                    request.OperationId,
                    actor,
                    new TurnInQuestCommand(request.CharacterId, questId, request.RewardChoiceDefinitionId?.Trim())),
                cancellationToken),
            QuestOperationKinds.AdminAcceptQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminAcceptQuestCommand>(
                    request.OperationId,
                    actor,
                    new AdminAcceptQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AdminCompleteQuestObjective => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminCompleteQuestObjectiveCommand>(
                    request.OperationId,
                    actor,
                    new AdminCompleteQuestObjectiveCommand(request.CharacterId, questId, objectiveId)),
                cancellationToken),
            QuestOperationKinds.AdminCompleteQuestStep => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminCompleteQuestStepCommand>(
                    request.OperationId,
                    actor,
                    new AdminCompleteQuestStepCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AdminMakeQuestReady => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminMakeQuestReadyCommand>(
                    request.OperationId,
                    actor,
                    new AdminMakeQuestReadyCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AdminResetQuest => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminResetQuestCommand>(
                    request.OperationId,
                    actor,
                    new AdminResetQuestCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AdminResetQuestChain => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminResetQuestChainCommand>(
                    request.OperationId,
                    actor,
                    new AdminResetQuestChainCommand(request.CharacterId, questId)),
                cancellationToken),
            QuestOperationKinds.AdminResetQuests => transactionService.ExecuteAsync(
                new ItemTransactionRequest<AdminResetQuestsCommand>(
                    request.OperationId,
                    actor,
                    new AdminResetQuestsCommand(request.CharacterId)),
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
            return SimulationQuestRejections.Map<SimulationItemOperationResponse>(result);
        }

        return ServiceResult<SimulationItemOperationResponse>.Ok(SimulationItemOperationResponse.From(
            result,
            await characterStates.ReadAsync(request.CharacterId, cancellationToken)));
    }

    private static bool IsOptionalContentIdentifier(string? value)
    {
        return value is null || Identifiers.IsContentIdentifier(value);
    }

    private static bool IsDevelopmentTool(string operationKind)
    {
        return operationKind is QuestOperationKinds.AdminAcceptQuest
            or QuestOperationKinds.AdminCompleteQuestObjective
            or QuestOperationKinds.AdminCompleteQuestStep
            or QuestOperationKinds.AdminMakeQuestReady
            or QuestOperationKinds.AdminResetQuest
            or QuestOperationKinds.AdminResetQuestChain
            or QuestOperationKinds.AdminResetQuests;
    }

    private static ServiceResult<SimulationItemOperationResponse> Invalid(string message)
    {
        return ServiceResult<SimulationItemOperationResponse>.BadRequest(
            ItemTransactionErrorCodes.SimulationOperationInvalid,
            message);
    }
}
