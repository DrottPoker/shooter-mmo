using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.GameService.Quests.Transactions.Domain;
using Mmorpg.Server.Common.Identity;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Quests.Transactions.Handlers;

// The quest commands: accepting and abandoning quests, granting start items again, talking,
// delivering, counting kills, and the turn-in. Availability and progress follow the shared
// quest rules, the quest log is the Quests module's, and start items and quest drops are
// protected quest items with the quest's lineage, which ending the quest removes. Quest
// actions only fill free slots or take the items an objective counts, so they lock the
// character without an expected revision and never fail because the player moved an item.
internal sealed partial class QuestCommandHandler(
    IQuestDefinitions questDefinitions,
    ICharacterQuestLog questLog,
    ICharacterWallet characterWallet)
    : IItemCommandHandler<AcceptQuestCommand>,
    IItemCommandHandler<AbandonQuestCommand>,
    IItemCommandHandler<RegrantQuestItemsCommand>,
    IItemCommandHandler<TalkToQuestNpcCommand>,
    IItemCommandHandler<DeliverQuestItemsCommand>,
    IItemCommandHandler<CreditQuestKillCommand>,
    IItemCommandHandler<TurnInQuestCommand>
{
    private readonly IQuestDefinitions questDefinitions = questDefinitions;
    private readonly ICharacterQuestLog questLog = questLog;
    private readonly ICharacterWallet characterWallet = characterWallet;

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AcceptQuestCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        QuestNpcAccess.Ensure(context.Authority, quest.GiverActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        // The character is locked as active, so it has a level.
        var level = await context.Placement.LoadCharacterLevelAsync(command.CharacterId, cancellationToken) ?? 0;
        RejectUnavailable(QuestRules.Evaluate(
            quest,
            level,
            log.ActiveQuests.Count,
            questDefinitions.MaximumActiveQuests,
            questId => log.ActiveQuests.Any(active => string.Equals(active.QuestId, questId, StringComparison.Ordinal)),
            questId => CompletionCount(log, questId)));

        await StartAsync(context, character, quest, "quest_accepted", cancellationToken);
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

        var removed = await QuestLineage.RemoveAbandonedAsync(
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
        QuestNpcAccess.Ensure(context.Authority, quest.GiverActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);

        var granted = await GrantMissingStartItemsAsync(
            context,
            character,
            quest,
            Array.IndexOf(quest.Steps, QuestProgression.CurrentStep(quest, active)),
            active.Progress,
            cancellationToken);
        context.Journal.AddMetadataAudit(
            granted == 0 ? "quest_start_items_present" : "quest_start_items_regranted",
            null,
            new { command.CharacterId, QuestId = quest.Id, GrantedStartItems = granted });
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        TalkToQuestNpcCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        var objective = FindObjective(quest, command.ObjectiveId, QuestObjectiveKindIds.TalkTo);
        QuestNpcAccess.Ensure(context.Authority, objective.NpcActorId);
        await context.Locks.LockCharacterStateForIdempotentEventAsync(command.CharacterId, cancellationToken);
        var (step, progress) = await OpenObjectiveAsync(context, command.CharacterId, quest, objective, cancellationToken);

        await CompleteObjectiveAsync(context, command.CharacterId, quest, step, objective, progress, cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_objective_talked",
            null,
            new { command.CharacterId, QuestId = quest.Id, ObjectiveId = objective.Id, objective.NpcActorId });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, [quest.Id], cancellationToken);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        DeliverQuestItemsCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        var objective = FindObjective(quest, command.ObjectiveId, QuestObjectiveKindIds.Deliver);
        QuestNpcAccess.Ensure(context.Authority, objective.NpcActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var (step, progress) = await OpenObjectiveAsync(context, command.CharacterId, quest, objective, cancellationToken);

        // A count raised since a part was handed over asks only for the rest.
        var quantity = QuestRules.RemainingDeliveryQuantity(objective, progress.GetValueOrDefault(objective.Id));
        var definition = await context.State.LoadDefinitionAsync(objective.ItemId, cancellationToken);
        await QuestLineage.TakeCarriedAsync(
            context,
            character,
            quest.Id,
            definition,
            quantity,
            "quest_item_delivered",
            cancellationToken);
        await CompleteObjectiveAsync(context, command.CharacterId, quest, step, objective, progress, cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_items_delivered",
            null,
            new
            {
                command.CharacterId,
                QuestId = quest.Id,
                ObjectiveId = objective.Id,
                objective.NpcActorId,
                DefinitionId = objective.ItemId,
                Quantity = quantity
            });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, [quest.Id], cancellationToken);
    }

    // The worker credits a Mob's kill to every character that its kill credit names; a kill
    // that no current step asks for changes nothing.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        CreditQuestKillCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        if (!Identifiers.IsContentIdentifier(command.MobActorId)
            || command.MobTags is null
            || command.MobTags.Count > WorldActorCompiler.MaximumTagsPerActor
            || command.MobTags.Any(tag => !Identifiers.IsContentIdentifier(tag)))
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.SimulationOperationInvalid,
                "A kill credit names the Mob's actor definition and at most "
                    + WorldActorCompiler.MaximumTagsPerActor
                    + " of its tags.");
        }

        await context.Locks.LockCharacterStateForIdempotentEventAsync(command.CharacterId, cancellationToken);
        var changed = await QuestProgression.CreditAsync(
            context,
            questDefinitions,
            questLog,
            command.CharacterId,
            objective => QuestRules.CountsKill(objective, command.MobActorId, command.MobTags) ? 1 : 0,
            cancellationToken);
        context.Journal.AddMetadataAudit(
            changed.Count == 0 ? "quest_kill_not_counted" : "quest_kill_counted",
            null,
            new { command.CharacterId, command.MobActorId, Quests = changed });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, changed, cancellationToken);
    }

    // Takes what the collect objectives count, removes what is left with the quest's lineage,
    // then grants the rewards, so the freed slots can hold them.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        TurnInQuestCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        QuestNpcAccess.Ensure(context.Authority, quest.TurnInActorId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);
        var step = QuestProgression.CurrentStep(quest, active);
        var collects = new List<(QuestObjectiveDefinition Objective, ResolvedItemDefinition Definition)>();
        var ready = ReferenceEquals(step, quest.Steps[^1]);
        foreach (var objective in step.Objectives)
        {
            if (QuestRules.StoresProgress(objective))
            {
                ready &= QuestRules.IsObjectiveComplete(objective, active.Progress.GetValueOrDefault(objective.Id));
                continue;
            }

            var definition = await context.State.LoadDefinitionAsync(objective.ItemId, cancellationToken);
            collects.Add((objective, definition));
            ready &= QuestRules.IsObjectiveComplete(
                objective,
                await QuestLineage.CountCarriedAsync(context, character, quest.Id, definition, cancellationToken));
        }

        if (!ready)
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestNotReady,
                "The quest is not ready to turn in.");
        }

        var choice = RewardChoice(quest, command.RewardChoiceDefinitionId);
        foreach (var (objective, definition) in collects)
        {
            await QuestLineage.TakeCarriedAsync(
                context,
                character,
                quest.Id,
                definition,
                objective.Count,
                "quest_item_turned_in",
                cancellationToken);
        }

        if (await QuestLineage.RemoveCompletedAsync(context, command.CharacterId, quest.Id, cancellationToken) > 0)
        {
            context.Journal.TouchCharacter(command.CharacterId);
        }

        QuestItemQuantityDefinition[] rewards = choice is null ? quest.RewardItems : [.. quest.RewardItems, choice];
        foreach (var reward in rewards)
        {
            var definition = await context.State.LoadDefinitionAsync(reward.DefinitionId, cancellationToken);
            await CarriedAcquisition.AcquireAsync(
                context,
                character,
                definition,
                reward.Quantity,
                "quest_reward",
                cancellationToken);
        }

        if (quest.RewardGold > 0)
        {
            var credit = await characterWallet.CreditAsync(
                context.Scope,
                command.CharacterId,
                quest.RewardGold,
                new CurrencyChangeSource(CurrencySourceKinds.QuestReward, quest.Id, context.OperationId),
                cancellationToken);
            if (!credit.Succeeded)
            {
                ItemTransactionContext.Reject(
                    ItemTransactionErrorCodes.CurrencyLimitExceeded,
                    "The quest's Gold reward would exceed the maximum balance.");
            }

            context.Journal.TouchCharacter(command.CharacterId);
        }

        await questLog.CompleteAsync(context.Scope, command.CharacterId, quest.Id, cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_turned_in",
            null,
            new
            {
                command.CharacterId,
                QuestId = quest.Id,
                quest.RewardGold,
                RewardItems = rewards.Select(reward => new { reward.DefinitionId, reward.Quantity }).ToArray()
            });
        await QuestLogChanges.ReportAsync(context, questLog, command.CharacterId, [quest.Id], cancellationToken);
    }

    private QuestDefinition FindQuest(string questId)
    {
        var quest = questDefinitions.Find(questId);
        if (quest is null)
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestNotFound,
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
                QuestTransactionErrorCodes.QuestNotActive,
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
                    QuestTransactionErrorCodes.QuestAlreadyActive,
                    "The quest is already active.");
                break;
            case QuestAvailability.AlreadyCompleted:
                ItemTransactionContext.Reject(
                    QuestTransactionErrorCodes.QuestAlreadyCompleted,
                    "The quest was already completed and cannot be repeated.");
                break;
            case QuestAvailability.MissingRequiredQuest:
                ItemTransactionContext.Reject(
                    QuestTransactionErrorCodes.QuestRequirementsNotMet,
                    "A quest that this quest requires is not completed yet.");
                break;
            case QuestAvailability.BelowMinimumLevel:
                ItemTransactionContext.Reject(
                    QuestTransactionErrorCodes.QuestLevelTooLow,
                    "The character's level is below the quest's minimum level.");
                break;
            case QuestAvailability.QuestLogFull:
                ItemTransactionContext.Reject(
                    QuestTransactionErrorCodes.QuestLogFull,
                    "The quest log is full.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(availability), availability, null);
        }
    }

    private static QuestObjectiveDefinition FindObjective(QuestDefinition quest, string objectiveId, string kind)
    {
        var objective = quest.Steps
            .SelectMany(step => step.Objectives)
            .FirstOrDefault(candidate => string.Equals(candidate.Id, objectiveId, StringComparison.Ordinal));
        if (objective is null || !string.Equals(objective.Kind, kind, StringComparison.Ordinal))
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestObjectiveInvalid,
                "The quest has no such objective of this kind.");
        }

        return objective;
    }

    // The objective must be part of the active quest's current step and not complete yet.
    private async Task<(QuestStepDefinition Step, Dictionary<string, int> Progress)> OpenObjectiveAsync(
        ItemTransactionContext context,
        Guid characterId,
        QuestDefinition quest,
        QuestObjectiveDefinition objective,
        CancellationToken cancellationToken)
    {
        var log = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        var active = RequireActive(log, quest.Id);
        var step = QuestProgression.CurrentStep(quest, active);
        if (!step.Objectives.Contains(objective))
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestObjectiveInvalid,
                "The objective is not part of the quest's current step.");
        }

        if (QuestRules.IsObjectiveComplete(objective, active.Progress.GetValueOrDefault(objective.Id)))
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestObjectiveComplete,
                "The objective is already complete.");
        }

        return (step, new Dictionary<string, int>(active.Progress, StringComparer.Ordinal));
    }

    private async Task CompleteObjectiveAsync(
        ItemTransactionContext context,
        Guid characterId,
        QuestDefinition quest,
        QuestStepDefinition step,
        QuestObjectiveDefinition objective,
        Dictionary<string, int> progress,
        CancellationToken cancellationToken)
    {
        progress[objective.Id] = objective.Count;
        await questLog.SetProgressAsync(
            context.Scope,
            characterId,
            quest.Id,
            new Dictionary<string, int>(StringComparer.Ordinal) { [objective.Id] = objective.Count },
            cancellationToken);
        await QuestProgression.AdvanceIfCompleteAsync(context, questLog, characterId, quest, step, progress, cancellationToken);
    }

    private static QuestItemQuantityDefinition? RewardChoice(QuestDefinition quest, string? definitionId)
    {
        if (quest.RewardChoiceItems.Length == 0)
        {
            if (!string.IsNullOrEmpty(definitionId))
            {
                ItemTransactionContext.Reject(
                    QuestTransactionErrorCodes.QuestRewardChoiceInvalid,
                    "The quest offers no reward to choose from.");
            }

            return null;
        }

        if (string.IsNullOrEmpty(definitionId))
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestRewardChoiceRequired,
                "Choose one of the quest's rewards.");
        }

        var choice = quest.RewardChoiceItems.FirstOrDefault(item =>
            string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
        if (choice is null)
        {
            ItemTransactionContext.Reject(
                QuestTransactionErrorCodes.QuestRewardChoiceInvalid,
                "The quest does not offer this reward.");
        }

        return choice;
    }

    // Grants the start items and starts the quest at its first step.
    private async Task StartAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        QuestDefinition quest,
        string auditKind,
        CancellationToken cancellationToken)
    {
        var granted = await GrantMissingStartItemsAsync(
            context,
            character,
            quest,
            0,
            new Dictionary<string, int>(StringComparer.Ordinal),
            cancellationToken);
        var firstStep = quest.Steps[0];
        await questLog.AcceptAsync(
            context.Scope,
            character.CharacterId,
            quest.Id,
            firstStep.Id,
            QuestProgression.CountedObjectiveIds(firstStep),
            cancellationToken);
        context.Journal.AddMetadataAudit(
            auditKind,
            null,
            new { character.CharacterId, QuestId = quest.Id, StepId = firstStep.Id, GrantedStartItems = granted });
        await QuestLogChanges.ReportAsync(context, questLog, character.CharacterId, [quest.Id], cancellationToken);
    }

    // Grants the start items that the quest still needs at its current step and the character
    // does not own, all or nothing, and returns the granted quantity.
    private static async Task<int> GrantMissingStartItemsAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        QuestDefinition quest,
        int currentStepIndex,
        IReadOnlyDictionary<string, int> progress,
        CancellationToken cancellationToken)
    {
        var granted = 0;
        foreach (var startItem in quest.StartItems)
        {
            var owned = await QuestLineage.CountOwnedAsync(
                context,
                character.CharacterId,
                quest.Id,
                startItem.DefinitionId,
                cancellationToken);
            var missing = QuestRules.MissingStartItemQuantity(
                quest,
                startItem,
                currentStepIndex,
                objectiveId => progress.GetValueOrDefault(objectiveId),
                owned);
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
