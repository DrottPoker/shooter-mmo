using System.Text.Json.Serialization;

namespace AuthService.Items;

public enum ItemTransactionAuthority
{
    Account = 1,
    System = 2,
    SimulationWorker = 3
}

public sealed record ItemTransactionActor(
    ItemTransactionAuthority Authority,
    Guid? AccountId,
    bool RequiresOfflineCharacter = false,
    SimulationItemTransactionAuthority? Simulation = null)
{
    public static ItemTransactionActor ForAccount(Guid accountId)
    {
        return new ItemTransactionActor(ItemTransactionAuthority.Account, accountId);
    }

    public static ItemTransactionActor ForOfflineAccount(Guid accountId)
    {
        return new ItemTransactionActor(
            ItemTransactionAuthority.Account,
            accountId,
            RequiresOfflineCharacter: true);
    }

    public static ItemTransactionActor ForSystem()
    {
        return new ItemTransactionActor(ItemTransactionAuthority.System, null);
    }

    public static ItemTransactionActor ForSimulationWorker(
        Guid accountId,
        Guid characterId,
        Guid simulationSessionId,
        string workerId,
        string workerRuntimeId,
        string shardId,
        string sessionTokenHash,
        ItemTransactionLiveAccess liveAccess)
    {
        return new ItemTransactionActor(
            ItemTransactionAuthority.SimulationWorker,
            accountId,
            Simulation: new SimulationItemTransactionAuthority(
                simulationSessionId,
                characterId,
                workerId,
                workerRuntimeId,
                shardId,
                sessionTokenHash,
                liveAccess));
    }
}

public sealed record SimulationItemTransactionAuthority(
    Guid SimulationSessionId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    [property: JsonIgnore] string SessionTokenHash,
    [property: JsonIgnore] ItemTransactionLiveAccess LiveAccess);

public sealed record ItemTransactionLiveAccess(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    bool QuestNpc = false)
{
    public static ItemTransactionLiveAccess None { get; } = new(false, false, false, false);
}

public sealed record ItemTransactionRequest<TCommand>(
    Guid OperationId,
    ItemTransactionActor Actor,
    TCommand Command)
    where TCommand : notnull;

public sealed record ItemRevisionExpectation(
    Guid ItemInstanceId,
    long Revision);

public sealed record CharacterRevisionExpectation(
    Guid CharacterId,
    long Revision);

public sealed record GrantItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string DefinitionId,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex,
    string? PolicySourceKind = null,
    string? PolicySourceId = null);

public sealed record RelocateItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

public sealed record EquipItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string EquipmentSlotId);

public sealed record UnequipItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

public sealed record SplitItemStackCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

public sealed record MergeItemStacksCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid SourceItemInstanceId,
    long ExpectedSourceItemRevision,
    Guid TargetItemInstanceId,
    long ExpectedTargetItemRevision);

public sealed record SwapContainerItemsCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid FirstItemInstanceId,
    long ExpectedFirstItemRevision,
    Guid SecondItemInstanceId,
    long ExpectedSecondItemRevision);

public sealed record ConsumeItemQuantityCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

public sealed record DestroyItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string Reason);

public sealed record SwapBagAggregatesCommand(
    Guid FirstCharacterId,
    long? ExpectedFirstCharacterRevision,
    Guid FirstBagItemInstanceId,
    long ExpectedFirstBagRevision,
    long ExpectedFirstBagContentsRevision,
    Guid SecondCharacterId,
    long? ExpectedSecondCharacterRevision,
    Guid SecondBagItemInstanceId,
    long ExpectedSecondBagRevision,
    long ExpectedSecondBagContentsRevision);

public sealed record AddRecoveryDeliveryCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string SourceKind,
    string SourceEventId,
    DateTime? AvailableAt,
    DateTime? ExpiresAt,
    IReadOnlyList<ItemRevisionExpectation> Items);

public sealed record ClaimRecoveryDeliveryCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid RecoveryDeliveryId,
    long ExpectedRecoveryDeliveryRevision,
    Guid DestinationContainerId,
    IReadOnlyList<ItemRevisionExpectation> Items);

public sealed record ChangeSecureContainerTierCommand(
    Guid AccountId,
    long ExpectedEntitlementRevision,
    string TierId,
    IReadOnlyList<CharacterRevisionExpectation> CharacterRevisions);

public sealed record ApplyItemPolicyCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string PolicyKind,
    string SourceKind,
    string SourceId,
    long Price = 0);

public sealed record RemoveInsurancePolicyCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision);

public sealed record AbandonQuestItemsCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string QuestGrantId);

public sealed record ProcessPlayerDeathCommand(
    Guid DeathEventId,
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey);

public sealed record ExpireCorpseCommand(Guid CorpseId);

public sealed record LootCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record LootCorpsePartialStackCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record DepositCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record DepositCorpsePartialStackCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record MoveCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record MoveCorpsePartialStackCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

public sealed record SwapCorpseBagCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid CorpseBagItemInstanceId,
    long ExpectedCorpseBagRevision,
    Guid CorpseBagContentsContainerId,
    long ExpectedCorpseBagContentsRevision,
    Guid PlayerBagItemInstanceId,
    long ExpectedPlayerBagRevision,
    Guid PlayerBagContentsContainerId,
    long ExpectedPlayerBagContentsRevision);

public sealed record ItemTransactionResult(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionContainerRevision> ContainerRevisions,
    IReadOnlyList<ItemTransactionItemRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    long? SecureContainerEntitlementRevision);

public sealed record ItemTransactionError(
    string Code,
    string Message);

public sealed record ItemTransactionCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity);

public sealed record ItemTransactionItemRevision(
    Guid ItemInstanceId,
    long Revision);

public sealed record ItemTransactionContainerRevision(
    Guid ContainerId,
    long Revision);

public static class ItemOperationKinds
{
    public const string Grant = "grant";
    public const string Relocate = "relocate";
    public const string Equip = "equip";
    public const string Unequip = "unequip";
    public const string SplitStack = "split_stack";
    public const string MergeStacks = "merge_stacks";
    public const string SwapContainerItems = "swap_container_items";
    public const string ConsumeQuantity = "consume_quantity";
    public const string Destroy = "destroy";
    public const string SwapBagAggregates = "swap_bag_aggregates";
    public const string AddRecoveryDelivery = "add_recovery_delivery";
    public const string ClaimRecoveryDelivery = "claim_recovery_delivery";
    public const string ChangeSecureContainerTier = "change_secure_container_tier";
    public const string ApplyItemPolicy = "apply_item_policy";
    public const string RemoveInsurancePolicy = "remove_insurance_policy";
    public const string AbandonQuestItems = "abandon_quest_items";
    public const string ProcessPlayerDeath = "process_player_death";
    public const string ExpireCorpse = "expire_corpse";
    public const string LootCorpseItem = "loot_corpse_item";
    public const string LootCorpsePartialStack = "loot_corpse_partial_stack";
    public const string DepositCorpseItem = "deposit_corpse_item";
    public const string DepositCorpsePartialStack = "deposit_corpse_partial_stack";
    public const string MoveCorpseItem = "move_corpse_item";
    public const string MoveCorpsePartialStack = "move_corpse_partial_stack";
    public const string SwapCorpseBag = "swap_corpse_bag";
}
