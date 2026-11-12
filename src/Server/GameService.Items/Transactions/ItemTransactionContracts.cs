using System.Collections.Immutable;
using System.Text.Json;
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

// What the worker validated for one live operation: the city services as flags, and the
// capability claims of the commands above the kernel, each a capability kind with the id the
// worker validated for it, such as the vendor profile of an NPC's vendor capability. The worker
// sets them from what it validated, never from a client value.
internal sealed record ItemTransactionLiveAccess(
    bool Bank,
    bool RecoveryStorage,
    bool InsuranceNpc)
{
    public static ItemTransactionLiveAccess None { get; } = new(false, false, false);

    public ImmutableDictionary<string, string> Capabilities { get; init; } =
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    // The same access with a claim of the kind, which replaces an earlier claim of that kind.
    public ItemTransactionLiveAccess WithCapability(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(id);
        return this with { Capabilities = Capabilities.SetItem(kind, id) };
    }
}

internal sealed record ItemTransactionRequest<TCommand>(
    Guid OperationId,
    ItemTransactionActor Actor,
    TCommand Command)
    where TCommand : notnull;

internal sealed record ItemRevisionExpectation(
    Guid ItemInstanceId,
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

// Equips the whole item, or with a quantity that part of a stack into an empty slot, such as
// some arrows. A whole equip leaves the quantity out of the stored request.
internal sealed record EquipItemCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string EquipmentSlotId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Quantity = null);

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

// Merges the whole source stack, or with a quantity that part of it, into the target stack. A
// whole merge leaves the quantity out of the stored request.
internal sealed record MergeItemStacksCommand(
    Guid CharacterId,
    long? ExpectedCharacterRevision,
    Guid SourceItemInstanceId,
    long ExpectedSourceItemRevision,
    Guid TargetItemInstanceId,
    long ExpectedTargetItemRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Quantity = null);

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
    IReadOnlyList<MobCorpseLootEntryRequest> Loot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OwnerCharacterId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? LootGroupId = null);

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
    long? ExpectedTargetItemRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuestId = null);

internal sealed record MobLootGrantEntry(
    Guid GrantId,
    string DefinitionId,
    int Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuestId = null);

// Grants every loot entry of a live Mob corpse that fits, whole, as CorpseLootAllPlanner plans
// it. An entry that an earlier operation already granted is left alone and reported as
// granted, so the worker can take it out of the corpse.
internal sealed record GrantMobLootAllCommand(
    Guid CharacterId,
    long ExpectedCharacterRevision,
    Guid SourceCorpseId,
    string SourceActorDefinitionId,
    IReadOnlyList<MobLootGrantEntry> Loot);

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

// Loots a corpse stack straight into an equipment slot: the whole stack, or with a quantity that
// part of it, which tops up the equipped stack or fills the empty slot. The target is the item
// the character had equipped in that slot when the client looked, or null for an empty slot. A
// whole loot leaves the quantity out of the stored request.
internal sealed record LootCorpseItemToEquipmentCommand(
    Guid CharacterId,
    Guid CorpseId,
    Guid ItemInstanceId,
    long ExpectedItemRevision,
    string EquipmentSlotId,
    Guid? TargetItemInstanceId,
    long? ExpectedTargetItemRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Quantity = null);

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

// Loots everything that fits from the corpse at the revision the client saw, as
// CorpseLootAllPlanner plans it.
internal sealed record LootAllCorpseItemsCommand(
    Guid CharacterId,
    Guid CorpseId,
    long ExpectedCorpseRevision);

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

internal sealed record ItemTransactionResult(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionContainerRevision> ContainerRevisions,
    IReadOnlyList<ItemTransactionItemRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    IReadOnlyList<ItemTransactionItemChange>? ItemChanges = null)
{
    // The named sections that commands above the kernel add through ItemChangeJournal.SetSection,
    // each a top-level key of the stored JSON. The kernel only stores and replays them; the
    // code that writes a section reads it.
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Sections { get; init; }

    // A section, or null when the result has none of that name. A stored null counts as none.
    public JsonElement? Section(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Sections is not null
            && Sections.TryGetValue(name, out var section)
            && section.ValueKind != JsonValueKind.Null
            ? section
            : null;
    }
}

internal sealed record ItemTransactionError(
    string Code,
    string Message);

// Currency is the balance after the commit; results stored before it existed have none.
internal sealed record ItemTransactionCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity,
    long? Currency = null);

// The final state of one item the transaction changed. A removed item has no definition,
// and an equipped item no container. Policies are the active ones, as snapshots show them.
// A Bag in the Bag slot names its contents container and its revision.
internal sealed record ItemTransactionItemChange(
    Guid ItemInstanceId,
    bool Removed,
    string? DefinitionId,
    int Quantity,
    long Revision,
    Guid? ContainerId,
    int? ContainerSlotIndex,
    Guid? EquippedCharacterId,
    string? EquipmentSlotId,
    IReadOnlyList<ItemPolicySummaryResponse> Policies,
    Guid? BagContentsContainerId = null,
    long? BagContentsRevision = null);

internal sealed record ItemTransactionItemRevision(
    Guid ItemInstanceId,
    long Revision);

internal sealed record ItemTransactionContainerRevision(
    Guid ContainerId,
    long Revision);

// The persisted operation kinds. The quest and economy kinds are declared with their commands in
// QuestTransactionContracts.cs and EconomyTransactionContracts.cs.
internal static partial class ItemOperationKinds
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
    public const string ApplyItemPolicy = "apply_item_policy";
    public const string RemoveInsurancePolicy = "remove_insurance_policy";
    public const string ProcessPlayerDeath = "process_player_death";
    public const string CreatePersistentMobCorpse = "create_persistent_mob_corpse";
    public const string GrantMobLoot = "grant_mob_loot";
    public const string GrantMobLootAll = "grant_mob_loot_all";
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
    public const string LootAllCorpseItems = "loot_all_corpse_items";
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
