using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

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
                ItemOperationKinds.AbandonQuestItems,
                static command => command.CharacterId,
                new QuestItemCommandHandler())
            {
                OperationallySignificant = true,
                PolicyAction = true
            },
            new ItemCommandRegistration<AcceptQuestCommand>(
                ItemOperationKinds.AcceptQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AbandonQuestCommand>(
                ItemOperationKinds.AbandonQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<RegrantQuestItemsCommand>(
                ItemOperationKinds.RegrantQuestItems,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<TalkToQuestNpcCommand>(
                ItemOperationKinds.TalkToQuestNpc,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<DeliverQuestItemsCommand>(
                ItemOperationKinds.DeliverQuestItems,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<CreditQuestKillCommand>(
                ItemOperationKinds.CreditQuestKill,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<TurnInQuestCommand>(
                ItemOperationKinds.TurnInQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminAcceptQuestCommand>(
                ItemOperationKinds.AdminAcceptQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminCompleteQuestObjectiveCommand>(
                ItemOperationKinds.AdminCompleteQuestObjective,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminCompleteQuestStepCommand>(
                ItemOperationKinds.AdminCompleteQuestStep,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminMakeQuestReadyCommand>(
                ItemOperationKinds.AdminMakeQuestReady,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestCommand>(
                ItemOperationKinds.AdminResetQuest,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestChainCommand>(
                ItemOperationKinds.AdminResetQuestChain,
                static command => command.CharacterId,
                quest),
            new ItemCommandRegistration<AdminResetQuestsCommand>(
                ItemOperationKinds.AdminResetQuests,
                static command => command.CharacterId,
                quest)
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
