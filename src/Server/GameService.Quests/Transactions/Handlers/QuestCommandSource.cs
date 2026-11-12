using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Quests.Transactions.Handlers;

// The quest commands, the Admin menu's quest tools, and the removal of a quest grant's items.
internal sealed class QuestCommandSource : IItemCommandSource
{
    public QuestCommandSource(
        IQuestDefinitions questDefinitions,
        ICharacterQuestLog questLog,
        ICharacterWallet characterWallet)
    {
        var quest = new QuestCommandHandler(questDefinitions, questLog, characterWallet);
        Registrations =
        [
            new ItemCommandRegistration<AbandonQuestItemsCommand>(
                QuestOperationKinds.AbandonQuestItems,
                static command => command.CharacterId,
                new QuestItemCommandHandler())
            {
                OperationallySignificant = true,
                PolicyAction = true
            },
            new ItemCommandRegistration<AcceptQuestCommand>(
                QuestOperationKinds.AcceptQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AbandonQuestCommand>(
                QuestOperationKinds.AbandonQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<RegrantQuestItemsCommand>(
                QuestOperationKinds.RegrantQuestItems,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<TalkToQuestNpcCommand>(
                QuestOperationKinds.TalkToQuestNpc,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<DeliverQuestItemsCommand>(
                QuestOperationKinds.DeliverQuestItems,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<CreditQuestKillCommand>(
                QuestOperationKinds.CreditQuestKill,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<TurnInQuestCommand>(
                QuestOperationKinds.TurnInQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminAcceptQuestCommand>(
                QuestOperationKinds.AdminAcceptQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminCompleteQuestObjectiveCommand>(
                QuestOperationKinds.AdminCompleteQuestObjective,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminCompleteQuestStepCommand>(
                QuestOperationKinds.AdminCompleteQuestStep,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminMakeQuestReadyCommand>(
                QuestOperationKinds.AdminMakeQuestReady,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestCommand>(
                QuestOperationKinds.AdminResetQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestChainCommand>(
                QuestOperationKinds.AdminResetQuestChain,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestsCommand>(
                QuestOperationKinds.AdminResetQuests,
                static command => command.CharacterId,
                quest)
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
