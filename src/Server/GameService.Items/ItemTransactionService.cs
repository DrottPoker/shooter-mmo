using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Items.Transactions.Handlers;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items;

internal sealed partial class ItemTransactionService(
    NpgsqlDataSource dataSource,
    ICharacterLocks characterLocks,
    ICharacterWallet characterWallet,
    ISimulationSessionAuthority simulationSessionAuthority,
    ISimulationWorkerAuthority simulationWorkerAuthority,
    ISimulationPresence simulationPresence,
    ItemOperationsMetrics? operationsMetrics = null,
    ItemOperationsOptions? itemOperationsOptions = null,
    ILogger<ItemTransactionService>? logger = null,
    IHttpContextAccessor? httpContextAccessor = null)
{
    private readonly RecoveryExpiryCommandHandler recoveryExpiry = new();
    private readonly ItemDestructionCommandHandler itemDestruction = new();
    private readonly ItemEquipmentCommandHandler itemEquipment = new();
    private readonly ItemRelocationCommandHandler itemRelocation = new();
    private readonly ItemStackCommandHandler itemStack = new();
    private readonly ItemGrantCommandHandler itemGrant = new();
    private readonly QuestItemCommandHandler questItem = new();
    private readonly ItemPolicyCommandHandler itemPolicy = new(characterWallet);
    private readonly BagAggregateCommandHandler bagAggregate = new();
    private readonly RecoveryDeliveryCommandHandler recoveryDelivery = new();
    private readonly SecureContainerTierCommandHandler secureContainerTier = new();
    private readonly MobCorpseCommandHandler mobCorpse = new();
    private readonly MobLootGrantCommandHandler mobLootGrant = new();
    private readonly CorpseExpiryCommandHandler corpseExpiry = new();
    private readonly ItemTransactionKernel kernel = new(
        dataSource,
        itemOperationsOptions ?? ItemOperationsOptions.CreateDefaults(),
        operationsMetrics,
        new ItemTransactionTelemetry(operationsMetrics, logger, httpContextAccessor),
        new ItemOperationLedger(dataSource),
        new ItemTransactionDependencies(
            characterLocks,
            simulationSessionAuthority,
            simulationWorkerAuthority,
            simulationPresence));

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
        ItemTransactionRequest<ChangeSecureContainerTierCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ChangeSecureContainerTier,
            null,
            secureContainerTier,
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
}
