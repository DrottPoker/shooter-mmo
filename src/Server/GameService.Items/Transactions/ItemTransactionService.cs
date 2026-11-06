using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Items.Transactions.Handlers;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Quests.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Items;

// The item transaction facade: one ExecuteAsync overload per item command. Each overload
// routes its command to the kernel with its persisted operation kind, its actor character,
// and its handler. The constructor composes the kernel and the stateless handlers.
internal sealed class ItemTransactionService
{
    private readonly ItemGrantCommandHandler itemGrant = new();
    private readonly ItemRelocationCommandHandler itemRelocation = new();
    private readonly ItemEquipmentCommandHandler itemEquipment = new();
    private readonly ItemStackCommandHandler itemStack = new();
    private readonly AmmunitionConsumptionCommandHandler ammunitionConsumption = new();
    private readonly ItemDestructionCommandHandler itemDestruction = new();
    private readonly BagAggregateCommandHandler bagAggregate = new();
    private readonly RecoveryDeliveryCommandHandler recoveryDelivery = new();
    private readonly ItemPolicyCommandHandler itemPolicy;
    private readonly QuestItemCommandHandler questItem = new();
    private readonly QuestCommandHandler quest;
    private readonly PlayerDeathCommandHandler playerDeath = new();
    private readonly CorpseExpiryCommandHandler corpseExpiry = new();
    private readonly CorpseLootCommandHandler corpseLoot;
    private readonly CorpseDepositCommandHandler corpseDeposit = new();
    private readonly CorpseInternalMoveCommandHandler corpseInternalMove = new();
    private readonly CorpseBagSwapCommandHandler corpseBagSwap = new();
    private readonly CorpseLootAllCommandHandler corpseLootAll;
    private readonly MobCorpseCommandHandler mobCorpse;
    private readonly MobLootGrantCommandHandler mobLootGrant;
    private readonly MobLootAllGrantCommandHandler mobLootAllGrant;
    private readonly RecoveryExpiryCommandHandler recoveryExpiry = new();
    private readonly VendorCommandHandler vendor;
    private readonly GatherResourceCommandHandler gatherResource;
    private readonly ItemTransactionKernel kernel;

    public ItemTransactionService(
        NpgsqlDataSource dataSource,
        ICharacterLocks characterLocks,
        ICharacterWallet characterWallet,
        ISimulationSessionAuthority simulationSessionAuthority,
        ISimulationWorkerAuthority simulationWorkerAuthority,
        ISimulationPresence simulationPresence,
        IQuestDefinitions questDefinitions,
        ICharacterQuestLog questLog,
        ItemOperationsMetrics? operationsMetrics = null,
        ItemOperationsOptions? itemOperationsOptions = null,
        ILogger<ItemTransactionService>? logger = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        var simulationAuthority = new ItemSimulationAuthority(
            characterLocks,
            simulationSessionAuthority,
            simulationWorkerAuthority);
        itemPolicy = new ItemPolicyCommandHandler(characterWallet);
        vendor = new VendorCommandHandler(characterWallet);
        quest = new QuestCommandHandler(questDefinitions, questLog, characterWallet);
        gatherResource = new GatherResourceCommandHandler(questDefinitions, questLog);
        var questDrops = new QuestDrops(questDefinitions, questLog);
        corpseLoot = new CorpseLootCommandHandler(questDrops);
        corpseLootAll = new CorpseLootAllCommandHandler(questDrops);
        mobCorpse = new MobCorpseCommandHandler(simulationAuthority, questDrops);
        mobLootGrant = new MobLootGrantCommandHandler(questDrops);
        mobLootAllGrant = new MobLootAllGrantCommandHandler(questDrops);
        kernel = new ItemTransactionKernel(
            dataSource,
            itemOperationsOptions ?? ItemOperationsOptions.CreateDefaults(),
            operationsMetrics,
            new ItemTransactionTelemetry(operationsMetrics, logger, httpContextAccessor),
            new ItemOperationLedger(dataSource),
            simulationAuthority,
            new CharacterItemStateLocks(characterLocks, simulationPresence, operationsMetrics));
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            request.Command.CharacterId,
            itemGrant,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RelocateItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Relocate,
            request.Command.CharacterId,
            itemRelocation,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<EquipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Equip,
            request.Command.CharacterId,
            itemEquipment,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<UnequipItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Unequip,
            request.Command.CharacterId,
            itemEquipment,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SplitItemStackCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SplitStack,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<MergeItemStacksCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.MergeStacks,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapContainerItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SwapContainerItems,
            request.Command.CharacterId,
            itemRelocation,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ConsumeItemQuantityCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ConsumeQuantity,
            request.Command.CharacterId,
            itemStack,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ConsumeEquippedAmmunitionCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ConsumeEquippedAmmunition,
            request.Command.CharacterId,
            ammunitionConsumption,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DestroyItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Destroy,
            request.Command.CharacterId,
            itemDestruction,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapBagAggregatesCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SwapBagAggregates,
            request.Command.FirstCharacterId,
            bagAggregate,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AddRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AddRecoveryDelivery,
            request.Command.CharacterId,
            recoveryDelivery,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ClaimRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ClaimRecoveryDelivery,
            request.Command.CharacterId,
            recoveryDelivery,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ApplyItemPolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ApplyItemPolicy,
            request.Command.CharacterId,
            itemPolicy,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RemoveInsurancePolicyCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.RemoveInsurancePolicy,
            request.Command.CharacterId,
            itemPolicy,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AbandonQuestItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AbandonQuestItems,
            request.Command.CharacterId,
            questItem,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AcceptQuestCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AcceptQuest,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AbandonQuestCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AbandonQuest,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<RegrantQuestItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.RegrantQuestItems,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<TalkToQuestNpcCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.TalkToQuestNpc,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DeliverQuestItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.DeliverQuestItems,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<CreditQuestKillCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.CreditQuestKill,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<TurnInQuestCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.TurnInQuest,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminAcceptQuestCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminAcceptQuest,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminCompleteQuestObjectiveCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminCompleteQuestObjective,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminCompleteQuestStepCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminCompleteQuestStep,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminMakeQuestReadyCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminMakeQuestReady,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminResetQuestCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminResetQuest,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminResetQuestChainCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminResetQuestChain,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<AdminResetQuestsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.AdminResetQuests,
            request.Command.CharacterId,
            quest,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ProcessPlayerDeathCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ProcessPlayerDeath,
            request.Command.CharacterId,
            playerDeath,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ExpireCorpse,
            null,
            corpseExpiry,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootCorpseItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.LootCorpseItem,
            request.Command.CharacterId,
            corpseLoot,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootCorpsePartialStackCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.LootCorpsePartialStack,
            request.Command.CharacterId,
            corpseLoot,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootCorpseItemToEquipmentCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.LootCorpseItemToEquipment,
            request.Command.CharacterId,
            corpseLoot,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DepositCorpseItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.DepositCorpseItem,
            request.Command.CharacterId,
            corpseDeposit,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<DepositCorpsePartialStackCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.DepositCorpsePartialStack,
            request.Command.CharacterId,
            corpseDeposit,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<MoveCorpseItemCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.MoveCorpseItem,
            request.Command.CharacterId,
            corpseInternalMove,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<MoveCorpsePartialStackCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.MoveCorpsePartialStack,
            request.Command.CharacterId,
            corpseInternalMove,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SwapCorpseBagCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.SwapCorpseBag,
            request.Command.CharacterId,
            corpseBagSwap,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<LootAllCorpseItemsCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.LootAllCorpseItems,
            request.Command.CharacterId,
            corpseLootAll,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<CreatePersistentMobCorpseCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.CreatePersistentMobCorpse,
            null,
            mobCorpse,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantMobLootCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.GrantMobLoot,
            request.Command.CharacterId,
            mobLootGrant,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GrantMobLootAllCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.GrantMobLootAll,
            request.Command.CharacterId,
            mobLootAllGrant,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ExpireRecoveryDelivery,
            null,
            recoveryExpiry,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<PurchaseFromVendorCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.VendorPurchase,
            request.Command.CharacterId,
            vendor,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<SellToVendorCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.VendorSale,
            request.Command.CharacterId,
            vendor,
            cancellationToken);
    }

    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<GatherResourceCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.GatherResource,
            request.Command.CharacterId,
            gatherResource,
            cancellationToken);
    }

    // The presentation key of every player corpse, also used by corpse tooling and fixtures.
    internal static string DefaultPlayerCorpsePresentationKey =>
        CorpseTransformRules.PlayerCorpsePresentationKey;
}
