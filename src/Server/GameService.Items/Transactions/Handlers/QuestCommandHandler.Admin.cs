using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Quests.Contracts;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Quests;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The development quest tools of the Admin menu, which the quest route runs only in the
// Development environment. They go around the givers, the NPCs, the level, and the required
// quests, but keep the quest log valid, so the normal quest actions go on from wherever they
// leave a quest.
internal sealed partial class QuestCommandHandler :
    IItemCommandHandler<AdminAcceptQuestCommand>,
    IItemCommandHandler<AdminCompleteQuestObjectiveCommand>,
    IItemCommandHandler<AdminCompleteQuestStepCommand>,
    IItemCommandHandler<AdminMakeQuestReadyCommand>,
    IItemCommandHandler<AdminResetQuestCommand>,
    IItemCommandHandler<AdminResetQuestChainCommand>,
    IItemCommandHandler<AdminResetQuestsCommand>
{
    // An active quest, a completed quest that does not repeat, and a full quest log still
    // refuse, so the stored state stays one the normal rules could reach.
    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AdminAcceptQuestCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        var quest = FindQuest(command.QuestId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        if (log.ActiveQuests.Any(active => string.Equals(active.QuestId, quest.Id, StringComparison.Ordinal)))
        {
            RejectUnavailable(QuestAvailability.AlreadyActive);
        }

        if (!quest.Repeatable && CompletionCount(log, quest.Id) > 0)
        {
            RejectUnavailable(QuestAvailability.AlreadyCompleted);
        }

        if (log.ActiveQuests.Count >= questDefinitions.MaximumActiveQuests)
        {
            RejectUnavailable(QuestAvailability.QuestLogFull);
        }

        await StartAsync(context, character, quest, "quest_admin_accepted", cancellationToken);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AdminCompleteQuestObjectiveCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        var quest = FindQuest(command.QuestId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);
        var step = QuestProgression.CurrentStep(quest, active);
        var objective = step.Objectives.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.ObjectiveId, StringComparison.Ordinal));
        if (objective is null)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.QuestObjectiveInvalid,
                "The objective is not part of the quest's current step.");
        }

        await CompleteForAdminAsync(context, character, quest, step, active, [objective], cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_admin_objective_completed",
            null,
            new { command.CharacterId, QuestId = quest.Id, StepId = step.Id, ObjectiveId = objective.Id });
        await ReportIfChangedAsync(context, command.CharacterId, log.Revision, quest.Id, cancellationToken);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AdminCompleteQuestStepCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        var quest = FindQuest(command.QuestId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);
        var step = QuestProgression.CurrentStep(quest, active);

        await CompleteForAdminAsync(context, character, quest, step, active, step.Objectives, cancellationToken);
        context.Journal.AddMetadataAudit(
            "quest_admin_step_completed",
            null,
            new { command.CharacterId, QuestId = quest.Id, StepId = step.Id });
        await ReportIfChangedAsync(context, command.CharacterId, log.Revision, quest.Id, cancellationToken);
    }

    public async Task ExecuteAsync(
        ItemTransactionContext context,
        AdminMakeQuestReadyCommand command,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        var quest = FindQuest(command.QuestId);
        var character = await context.Locks.LockCharacterStateForIdempotentEventAsync(
            command.CharacterId,
            cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken);
        var active = RequireActive(log, quest.Id);

        // Every completed step before the last moves the quest on, so the loop ends at the last.
        var step = QuestProgression.CurrentStep(quest, active);
        while (true)
        {
            await CompleteForAdminAsync(context, character, quest, step, active, step.Objectives, cancellationToken);
            if (ReferenceEquals(step, quest.Steps[^1]))
            {
                break;
            }

            active = RequireActive(
                await questLog.ReadAsync(context.Scope, command.CharacterId, cancellationToken),
                quest.Id);
            var next = QuestProgression.CurrentStep(quest, active);
            if (ReferenceEquals(next, step))
            {
                throw new InvalidOperationException(
                    $"Quest '{quest.Id}' did not move on from its completed step '{step.Id}'.");
            }

            step = next;
        }

        context.Journal.AddMetadataAudit(
            "quest_admin_made_ready",
            null,
            new { command.CharacterId, QuestId = quest.Id, StepId = step.Id });
        await ReportIfChangedAsync(context, command.CharacterId, log.Revision, quest.Id, cancellationToken);
    }

    public Task ExecuteAsync(
        ItemTransactionContext context,
        AdminResetQuestCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        return ResetAsync(
            context,
            command.CharacterId,
            new HashSet<string>(StringComparer.Ordinal) { quest.Id },
            cancellationToken);
    }

    public Task ExecuteAsync(
        ItemTransactionContext context,
        AdminResetQuestChainCommand command,
        CancellationToken cancellationToken)
    {
        var quest = FindQuest(command.QuestId);
        return ResetAsync(context, command.CharacterId, FollowUpsOf(quest.Id), cancellationToken);
    }

    public Task ExecuteAsync(
        ItemTransactionContext context,
        AdminResetQuestsCommand command,
        CancellationToken cancellationToken)
    {
        return ResetAsync(context, command.CharacterId, null, cancellationToken);
    }

    // Forgets the chosen quests that the log holds, or every quest it holds, with the items of
    // their lineage. Forgetting nothing changes nothing.
    private async Task ResetAsync(
        ItemTransactionContext context,
        Guid characterId,
        IReadOnlySet<string>? chosen,
        CancellationToken cancellationToken)
    {
        context.Authority.EnsureSimulationWorkerAuthority();
        await context.Locks.LockCharacterStateForIdempotentEventAsync(characterId, cancellationToken);
        var log = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        var forgotten = log.ActiveQuests.Select(quest => quest.QuestId)
            .Concat(log.CompletedQuests.Select(quest => quest.QuestId))
            .Distinct(StringComparer.Ordinal)
            .Where(questId => chosen is null || chosen.Contains(questId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (forgotten.Length == 0)
        {
            context.Journal.AddMetadataAudit(
                "quest_admin_reset_nothing",
                null,
                new { CharacterId = characterId, Quests = chosen?.Order(StringComparer.Ordinal).ToArray() });
            return;
        }

        var removed = 0;
        foreach (var questId in forgotten)
        {
            removed += await QuestLineageItems.RemoveResetAsync(context, characterId, questId, cancellationToken);
            await questLog.ForgetAsync(context.Scope, characterId, questId, cancellationToken);
        }

        if (removed > 0)
        {
            context.Journal.TouchCharacter(characterId);
        }

        context.Journal.AddMetadataAudit(
            "quest_admin_reset",
            null,
            new { CharacterId = characterId, Quests = forgotten, RemovedItemCount = removed });
        await QuestLogChanges.ReportAsync(context, questLog, characterId, forgotten, cancellationToken);
    }

    // Collect objectives count carried items, so filling them changes items alone and leaves
    // the quest log, and its change, as they were.
    private async Task ReportIfChangedAsync(
        ItemTransactionContext context,
        Guid characterId,
        long revisionBefore,
        string questId,
        CancellationToken cancellationToken)
    {
        var state = await questLog.ReadAsync(context.Scope, characterId, cancellationToken);
        if (state.Revision != revisionBefore)
        {
            context.Journal.SetQuestLogChange(QuestLogChanges.Of(characterId, state, [questId]));
        }
    }

    // The quest and every quest that requires it, directly or through another.
    private HashSet<string> FollowUpsOf(string questId)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal) { questId };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var quest in questDefinitions.Quests)
            {
                if (!chain.Contains(quest.Id)
                    && (quest.RequiredQuestIds ?? []).Any(chain.Contains))
                {
                    chain.Add(quest.Id);
                    grew = true;
                }
            }
        }

        return chain;
    }

    // Counted objectives reach their count and collect objectives get the items they still lack
    // into carried storage, quest items with the quest's lineage; a step that is then complete
    // moves on, as a normal completion does.
    private async Task CompleteForAdminAsync(
        ItemTransactionContext context,
        LockedCharacterState character,
        QuestDefinition quest,
        QuestStepDefinition step,
        ActiveQuestState active,
        IReadOnlyList<QuestObjectiveDefinition> objectives,
        CancellationToken cancellationToken)
    {
        var progress = new Dictionary<string, int>(active.Progress, StringComparer.Ordinal);
        var updates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var objective in objectives)
        {
            if (QuestRules.StoresProgress(objective))
            {
                if (!QuestRules.IsObjectiveComplete(objective, progress.GetValueOrDefault(objective.Id)))
                {
                    updates[objective.Id] = objective.Count;
                    progress[objective.Id] = objective.Count;
                }

                continue;
            }

            var definition = await context.State.LoadDefinitionAsync(objective.ItemId, cancellationToken);
            var missing = objective.Count
                - await QuestCarriedItems.CountAsync(context, character, quest.Id, definition, cancellationToken);
            if (missing <= 0)
            {
                continue;
            }

            if (string.Equals(definition.RuntimeDefinition.Category, ItemCategoryIds.QuestItem, StringComparison.Ordinal))
            {
                await CarriedAcquisition.AcquireWithLineageAsync(
                    context,
                    character,
                    definition,
                    missing,
                    ItemPolicySourceKinds.QuestGrant,
                    quest.Id,
                    "quest_admin_item",
                    cancellationToken);
            }
            else
            {
                await CarriedAcquisition.AcquireStacksAsync(
                    context,
                    character,
                    definition,
                    missing,
                    "quest_admin_item",
                    cancellationToken);
            }
        }

        if (updates.Count > 0)
        {
            await questLog.SetProgressAsync(context.Scope, character.CharacterId, quest.Id, updates, cancellationToken);
        }

        await QuestProgression.AdvanceIfCompleteAsync(
            context,
            questLog,
            character.CharacterId,
            quest,
            step,
            progress,
            cancellationToken);
    }
}
