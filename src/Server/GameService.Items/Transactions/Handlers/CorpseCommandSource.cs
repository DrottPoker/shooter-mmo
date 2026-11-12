using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Quests.Contracts;

namespace Mmorpg.GameService.Items.Transactions.Handlers;

// The corpse and death commands: player death and corpse expiry, the player corpse loot,
// deposit, move, Bag swap, and Loot all, and durable Mob corpses with the live Mob loot grants.
internal sealed class CorpseCommandSource : IItemCommandSource
{
    public CorpseCommandSource(
        ItemSimulationAuthority simulationAuthority,
        IQuestDefinitions questDefinitions,
        ICharacterQuestLog questLog)
    {
        // Quest drops belong to the quest commands. This source builds them until the quest
        // commands move to GameService.Quests and these handlers to the Corpses module.
        var questDrops = new QuestDrops(questDefinitions, questLog);
        var corpseLoot = new CorpseLootCommandHandler(questDrops);
        var corpseDeposit = new CorpseDepositCommandHandler();
        var corpseInternalMove = new CorpseInternalMoveCommandHandler();
        Registrations =
        [
            new ItemCommandRegistration<ProcessPlayerDeathCommand>(
                ItemOperationKinds.ProcessPlayerDeath,
                static command => command.CharacterId,
                new PlayerDeathCommandHandler())
            {
                RetriedSystemOperation = true,
                OperationallySignificant = true
            },
            new ItemCommandRegistration<ExpireCorpseCommand>(
                ItemOperationKinds.ExpireCorpse,
                static _ => null,
                new CorpseExpiryCommandHandler())
            {
                RetriedSystemOperation = true,
                OperationallySignificant = true
            },
            new ItemCommandRegistration<LootCorpseItemCommand>(
                ItemOperationKinds.LootCorpseItem,
                static command => command.CharacterId,
                corpseLoot),
            new ItemCommandRegistration<LootCorpsePartialStackCommand>(
                ItemOperationKinds.LootCorpsePartialStack,
                static command => command.CharacterId,
                corpseLoot),
            new ItemCommandRegistration<LootCorpseItemToEquipmentCommand>(
                ItemOperationKinds.LootCorpseItemToEquipment,
                static command => command.CharacterId,
                corpseLoot),
            new ItemCommandRegistration<DepositCorpseItemCommand>(
                ItemOperationKinds.DepositCorpseItem,
                static command => command.CharacterId,
                corpseDeposit),
            new ItemCommandRegistration<DepositCorpsePartialStackCommand>(
                ItemOperationKinds.DepositCorpsePartialStack,
                static command => command.CharacterId,
                corpseDeposit),
            new ItemCommandRegistration<MoveCorpseItemCommand>(
                ItemOperationKinds.MoveCorpseItem,
                static command => command.CharacterId,
                corpseInternalMove),
            new ItemCommandRegistration<MoveCorpsePartialStackCommand>(
                ItemOperationKinds.MoveCorpsePartialStack,
                static command => command.CharacterId,
                corpseInternalMove),
            new ItemCommandRegistration<SwapCorpseBagCommand>(
                ItemOperationKinds.SwapCorpseBag,
                static command => command.CharacterId,
                new CorpseBagSwapCommandHandler()),
            new ItemCommandRegistration<LootAllCorpseItemsCommand>(
                ItemOperationKinds.LootAllCorpseItems,
                static command => command.CharacterId,
                new CorpseLootAllCommandHandler(questDrops)),
            new ItemCommandRegistration<CreatePersistentMobCorpseCommand>(
                ItemOperationKinds.CreatePersistentMobCorpse,
                static _ => null,
                new MobCorpseCommandHandler(simulationAuthority, questDrops))
            {
                RetriedSystemOperation = true,
                OperationallySignificant = true
            },
            new ItemCommandRegistration<GrantMobLootCommand>(
                ItemOperationKinds.GrantMobLoot,
                static command => command.CharacterId,
                new MobLootGrantCommandHandler(questDrops))
            {
                RetriedSystemOperation = true
            },
            new ItemCommandRegistration<GrantMobLootAllCommand>(
                ItemOperationKinds.GrantMobLootAll,
                static command => command.CharacterId,
                new MobLootAllGrantCommandHandler(questDrops))
        ];
    }

    public IReadOnlyList<ItemCommandRegistration> Registrations { get; }
}
