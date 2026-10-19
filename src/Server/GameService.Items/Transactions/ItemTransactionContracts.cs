using System.Text.Json.Serialization;

namespace Mmorpg.GameService.Items;

internal enum ItemTransactionAuthority
{
    Account = 1,
    System = 2,
    SimulationWorker = 3
}

internal sealed record ItemTransactionActor(
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

internal sealed record SimulationItemTransactionAuthority(
    Guid SimulationSessionId,
    Guid CharacterId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    [property: JsonIgnore] string SessionTokenHash,
    [property: JsonIgnore] ItemTransactionLiveAccess LiveAccess);

// VendorProfileId names the vendor profile of the NPC whose vendor capability the worker
// validated for this operation, or is null without vendor access.
internal sealed record ItemTransactionLiveAccess(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc,
    bool QuestNpc = false,
    string? VendorProfileId = null)
{
    public static ItemTransactionLiveAccess None { get; } = new(false, false, false, false);
}

internal sealed record ItemTransactionRequest<TCommand>(
    Guid OperationId,
    ItemTransactionActor Actor,
    TCommand Command)
    where TCommand : notnull;

internal sealed record ItemRevisionExpectation(
    Guid ItemInstanceId,
    long Revision);

internal sealed record CharacterRevisionExpectation(
    Guid CharacterId,
    long Revision);

internal sealed record GrantItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string DefinitionId,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex,
    string? PolicySourceKind = null,
    string? PolicySourceId = null);

internal sealed record RelocateItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

internal sealed record EquipItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string EquipmentSlotId);

internal sealed record UnequipItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

internal sealed record SplitItemStackCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    Guid DestinationContainerId,
    int? DestinationSlotIndex);

internal sealed record MergeItemStacksCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid SourceItemInstanceId,
    long ExpectedSourceItemRevision,
    Guid TargetItemInstanceId,
    long ExpectedTargetItemRevision);

internal sealed record SwapContainerItemsCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid FirstItemInstanceId,
    long ExpectedFirstItemRevision,
    Guid SecondItemInstanceId,
    long ExpectedSecondItemRevision);

internal sealed record ConsumeItemQuantityCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

// One batch of shots that the SimulationWorker counted against the equipped ammunition.
internal sealed record ConsumeEquippedAmmunitionCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

internal sealed record DestroyItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string Reason);

internal sealed record SwapBagAggregatesCommand(
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

internal sealed record AddRecoveryDeliveryCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string SourceKind,
    string SourceEventId,
    DateTime? AvailableAt,
    DateTime? ExpiresAt,
    IReadOnlyList<ItemRevisionExpectation> Items);

internal sealed record ClaimRecoveryDeliveryCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid RecoveryDeliveryId,
    long ExpectedRecoveryDeliveryRevision,
    Guid DestinationContainerId,
    IReadOnlyList<ItemRevisionExpectation> Items);

internal sealed record ChangeSecureContainerTierCommand(
    Guid AccountId,
    long ExpectedEntitlementRevision,
    string TierId,
    IReadOnlyList<CharacterRevisionExpectation> CharacterRevisions);

internal sealed record ApplyItemPolicyCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string PolicyKind,
    string SourceKind,
    string SourceId,
    long Price = 0);

internal sealed record RemoveInsurancePolicyCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision);

internal sealed record AbandonQuestItemsCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    string QuestGrantId);

internal sealed record ProcessPlayerDeathCommand(
    Guid DeathEventId,
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DeathAmmunitionConsumption? AmmunitionConsumption = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DeathKiller? Killer = null);

// The shots the worker had not committed yet, consumed inside the death transaction.
internal sealed record DeathAmmunitionConsumption(
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity);

// Who dealt the lethal hit: a Mob by its actor definition id, or a character by its id.
internal sealed record DeathKiller(string Kind, string Id);

internal static class DeathKillerKinds
{
    public const string Mob = "mob";
    public const string Character = "character";
}

internal sealed record CreatePersistentMobCorpseCommand(
    Guid CorpseId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    string SourceActorDefinitionId,
    string SourceDisplayName,
    double PositionX,
    double PositionY,
    double PositionZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    string PresentationKey,
    double LifetimeSeconds,
    int WorkerHeartbeatTimeoutSeconds,
    IReadOnlyList<MobCorpseLootEntryRequest> Loot);

internal sealed record GrantMobLootCommand(
    Guid CharacterId,
    long ExpectedCharacterRevision,
    Guid GrantId,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    string DefinitionId,
    int Quantity,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    string? EquipmentSlotId = null,
    Guid? TargetItemInstanceId = null,
    long? ExpectedTargetItemRevision = null);

internal sealed record ExpireCorpseCommand(Guid CorpseId);

internal sealed record ExpireRecoveryDeliveryCommand(Guid RecoveryDeliveryId);

internal sealed record LootCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

// Loots a whole corpse stack straight into an equipment slot. The target is the item the
// character had equipped in that slot when the client looked, or null for an empty slot.
internal sealed record LootCorpseItemToEquipmentCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string EquipmentSlotId,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

internal sealed record LootCorpsePartialStackCommand(
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

internal sealed record DepositCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

internal sealed record DepositCorpsePartialStackCommand(
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

internal sealed record MoveCorpseItemCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    Guid DestinationContainerId,
    long ExpectedDestinationContainerRevision,
    int DestinationSlotIndex,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision);

internal sealed record MoveCorpsePartialStackCommand(
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

internal sealed record SwapCorpseBagCommand(
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

// Buys a quantity of one vendor offer into carried storage. ExpectedPrice is the total the
// client showed, so the player is never charged a price they did not see.
internal sealed record PurchaseFromVendorCommand(
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string VendorId,
    string DefinitionId,
    int Quantity,
    long ExpectedPrice);

// Sells a quantity of one carried item to a vendor. ExpectedPrice is the total the client
// showed.
internal sealed record SellToVendorCommand(
    Guid CharacterId,
    long ExpectedCharacterRevision,
    string VendorId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    int Quantity,
    long ExpectedPrice);

// Grants one gathering yield into carried storage. ResourceNodeId is the node's spawn instance
// id. SimulationWorker resolves the yield and the required tool kind from the resource
// profile and validates the live interaction;
// GameService checks the equipped tool in the same transaction. No expected character
// revision applies, because gathering must not fail when the player moves an item meanwhile.
internal sealed record GatherResourceCommand(
    Guid CharacterId,
    string ResourceProfileId,
    string ResourceNodeId,
    string RequiredToolKind,
    string YieldDefinitionId,
    int YieldQuantity);

internal sealed record ItemTransactionResult(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionContainerRevision> ContainerRevisions,
    IReadOnlyList<ItemTransactionItemRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    long? SecureContainerEntitlementRevision);

internal sealed record ItemTransactionError(
    string Code,
    string Message);

internal sealed record ItemTransactionCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity);

internal sealed record ItemTransactionItemRevision(
    Guid ItemInstanceId,
    long Revision);

internal sealed record ItemTransactionContainerRevision(
    Guid ContainerId,
    long Revision);

internal static class ItemOperationKinds
{
    public const string Grant = "grant";
    public const string Relocate = "relocate";
    public const string Equip = "equip";
    public const string Unequip = "unequip";
    public const string SplitStack = "split_stack";
    public const string MergeStacks = "merge_stacks";
    public const string SwapContainerItems = "swap_container_items";
    public const string ConsumeQuantity = "consume_quantity";
    public const string ConsumeEquippedAmmunition = "consume_equipped_ammunition";
    public const string Destroy = "destroy";
    public const string SwapBagAggregates = "swap_bag_aggregates";
    public const string AddRecoveryDelivery = "add_recovery_delivery";
    public const string ClaimRecoveryDelivery = "claim_recovery_delivery";
    public const string ChangeSecureContainerTier = "change_secure_container_tier";
    public const string ApplyItemPolicy = "apply_item_policy";
    public const string RemoveInsurancePolicy = "remove_insurance_policy";
    public const string AbandonQuestItems = "abandon_quest_items";
    public const string ProcessPlayerDeath = "process_player_death";
    public const string CreatePersistentMobCorpse = "create_persistent_mob_corpse";
    public const string GrantMobLoot = "grant_mob_loot";
    public const string ExpireCorpse = "expire_corpse";
    public const string ExpireRecoveryDelivery = "expire_recovery_delivery";
    public const string LootCorpseItem = "loot_corpse_item";
    public const string LootCorpsePartialStack = "loot_corpse_partial_stack";
    public const string LootCorpseItemToEquipment = "loot_corpse_item_to_equipment";
    public const string DepositCorpseItem = "deposit_corpse_item";
    public const string DepositCorpsePartialStack = "deposit_corpse_partial_stack";
    public const string MoveCorpseItem = "move_corpse_item";
    public const string MoveCorpsePartialStack = "move_corpse_partial_stack";
    public const string SwapCorpseBag = "swap_corpse_bag";
    public const string VendorPurchase = "vendor_purchase";
    public const string VendorSale = "vendor_sale";
    public const string GatherResource = "gather_resource";

    // System operations use derived or server-held ids and are retried until they
    // commit, so a rejection is returned without being stored for replay.
    public static bool IsRetriedSystemOperation(string operationKind)
    {
        return operationKind is ProcessPlayerDeath
            or CreatePersistentMobCorpse
            or GrantMobLoot
            or ExpireCorpse
            or ExpireRecoveryDelivery;
    }
}

internal static class ItemOperationIds
{
    // Player-chosen ids must be random version 4 UUIDs. Every derived system id is a
    // version 5 UUID, so a player can never claim an id a system operation needs.
    public static bool IsRandomClientOperationId(Guid operationId)
    {
        Span<byte> bytes = stackalloc byte[16];
        operationId.TryWriteBytes(bytes);
        return (bytes[7] & 0xf0) == 0x40
            && (bytes[8] & 0xc0) == 0x80;
    }
}
