using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// Accepting and abandoning quests and granting their start items again. Availability follows
// the shared quest rules, the quest log is the Quests module's, and start items are protected
// quest items with the quest's lineage, which ending the quest removes again. Quest actions
// only fill free slots or take lineage items, so they lock the character without an expected
// revision and never fail because the player moved an item meanwhile.
internal sealed class QuestCommandHandler(
    IQuestDefinitions questDefinitions,
    ICharacterQuestLog questLog)
    : IItemCommandHandler<AcceptQuestCommand>,
    IItemCommandHandler<AbandonQuestCommand>,
    IItemCommandHandler<RegrantQuestItemsCommand>
{
    private readonly IQuestDefinitions questDefinitions = questDefinitions;
    private readonly ICharacterQuestLog questLog = questLog;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AcceptQuestCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        context.Authority.EnsureQuestNpcAccess(quest.GiverActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var level = await context.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select {CharactersSql.LevelOf("@CharacterId")};",
            new { command.CharacterId },
            context.Transaction,
            cancellationToken: cancellationToken));
        RejectUnavailable(QuestRules.Evaluate(
            quest,
            level,
            log.ActiveQuests.Count,
            questDefinitions.MaximumActiveQuests,
            questId => log.ActiveQuests.Any(active => string.Equals(active.QuestId, questId, StringComparison.Ordinal)),
            questId => CompletionCount(log, questId)));

        var granted = await GrantMissingStartItemsAsync(
            context,
            character,
            quest,
            _ => 0,
            cancellationToken);
        var firstStep = quest.Steps[0];
        await questLog.AcceptAsync(
            context.Scope,
            command.CharacterId,
            quest.Id,
            firstStep.Id,
            CountedObjectiveIds(firstStep),
            cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_accepted",
            null,
            new { command.CharacterId, QuestId = quest.Id, StepId = firstStep.Id, GrantedStartItems = granted });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, [quest.Id], cancellationToken);
    }

    // Abandoning works anywhere from the quest log, so it needs the live character only.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AbandonQuestCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        var quest = FindQuest(command.QuestId);
        await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);

        var removed = await QuestLineageItems.RemoveAbandonedAsync(
            context,
            command.CharacterId,
            quest.Id,
            cancellationToken);
        if (removed > 0)
        {
            context.Journal.TouchCharacter(command.CharacterId);
        }

        await questLog.AbandonAsync(context.Scope, command.CharacterId, quest.Id, cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_abandoned",
            new { command.CharacterId, QuestId = quest.Id, active.StepId },
            new { command.CharacterId, QuestId = quest.Id, RemovedItemCount = removed });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, [quest.Id], cancellationToken);
    }

    // The giver grants what the quest still needs and the character no longer owns, so a
    // start item that a delivery already took is not granted again.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        RegrantQuestItemsCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        context.Authority.EnsureQuestNpcAccess(quest.GiverActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);

        var granted = await GrantMissingStartItemsAsync(
            context,
            character,
            quest,
            definitionId => DeliveredQuantity(quest, active, definitionId),
            cancellationToken);
        context.Journal.AddMetadataAudit(
            granted == 0 ? "quest_start_items_present" : "quest_start_items_regranted",
            null,
            new { command.CharacterId, QuestId = quest.Id, GrantedStartItems = granted });
    }

    private QuestDefinition FindQuest(string questId)
    {
        var quest = questDefinitions.Find(questId);
        if (quest is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestNotFound,
                "The quest catalog holds no such quest.");
        }

        return quest;
    }

    private static ActiveQuestState RequireActive(CharacterQuestLogState log, string questId)
    {
        var active = log.ActiveQuests.FirstOrDefault(quest =>
            string.Equals(quest.QuestId, questId, StringComparison.Ordinal));
        if (active is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestNotActive,
                "The character does not have this quest active.");
        }

        return active;
    }

    private static int CompletionCount(CharacterQuestLogState log, string questId)
    {
        return log.CompletedQuests.FirstOrDefault(quest =>
            string.Equals(quest.QuestId, questId, StringComparison.Ordinal))?.CompletionCount ?? 0;
    }

    private static void RejectUnavailable(QuestAvailability availability)
    {
        switch (availability)
        {
            case QuestAvailability.Available:
                return;
            case QuestAvailability.AlreadyActive:
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestAlreadyActive,
                    "The quest is already active.");
                break;
            case QuestAvailability.AlreadyCompleted:
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestAlreadyCompleted,
                    "The quest was already completed and cannot be repeated.");
                break;
            case QuestAvailability.MissingRequiredQuest:
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestRequirementsNotMet,
                    "A quest that this quest requires is not completed yet.");
                break;
            case QuestAvailability.BelowMinimumLevel:
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestLevelTooLow,
                    "The character's level is below the quest's minimum level.");
                break;
            case QuestAvailability.QuestLogFull:
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestLogFull,
                    "The quest log is full.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(availability), availability, null);
        }
    }

    // Collect objectives count carried items, so only the other kinds store progress.
    private static string[] CountedObjectiveIds(QuestStepDefinition step)
    {
        return step.Objectives
            .Where(objective => !string.Equals(objective.Kind, QuestObjectiveKindIds.Collect, StringComparison.Ordinal))
            .Select(objective => objective.Id)
            .ToArray();
    }

    // The quantity of a definition that completed deliver objectives already took: every
    // delivery of an earlier step and the completed deliveries of the current step.
    private static int DeliveredQuantity(QuestDefinition quest, ActiveQuestState active, string definitionId)
    {
        var delivered = 0;
        foreach (var step in quest.Steps)
        {
            var current = string.Equals(step.Id, active.StepId, StringComparison.Ordinal);
            foreach (var objective in step.Objectives)
            {
                if (!string.Equals(objective.Kind, QuestObjectiveKindIds.Deliver, StringComparison.Ordinal)
                    || !string.Equals(objective.ItemId, definitionId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!current
                    || QuestRules.IsObjectiveComplete(
                        objective,
                        active.Progress.TryGetValue(objective.Id, out var progress) ? progress : 0))
                {
                    delivered += objective.Count;
                }
            }

            if (current)
            {
                break;
            }
        }

        return delivered;
    }

    // Grants the start items that the quest still needs and the character does not own,
    // all or nothing, and returns the granted quantity.
    private static async Task<int> GrantMissingStartItemsAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        QuestDefinition quest,
        Func<string, int> deliveredQuantity,
        CancellationToken cancellationToken)
    {
        var granted = 0;
        foreach (var startItem in quest.StartItems)
        {
            var needed = Math.Max(0, startItem.Quantity - deliveredQuantity(startItem.DefinitionId));
            var owned = await QuestLineageItems.CountOwnedAsync(
                context,
                character.CharacterId,
                quest.Id,
                startItem.DefinitionId,
                cancellationToken);
            var missing = needed - owned;
            if (missing <= 0)
            {
                continue;
            }

            var definition = await context.State.LoadDefinitionAsync(startItem.DefinitionId, cancellationToken);
            if (!string.Equals(definition.RuntimeDefinition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal)
                || !definition.RuntimeDefinition.DefaultPolicies.Contains(ItemPolicyIds.ProtectedOnDeath, StringComparer.Ordinal))
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.QuestGrantInvalid,
                    "A quest start item must be a protected quest item.");
            }

            await CarriedAcquisition.AcquireWithLineageAsync(
                context,
                character,
                definition,
                missing,
                ItemPolicySourceKinds.QuestGrant,
                quest.Id,
                "quest_start_item",
                cancellationToken);
            granted += missing;
        }

        return granted;
    }
}
