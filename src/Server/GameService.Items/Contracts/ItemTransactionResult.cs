using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items.Contracts;

public sealed record ItemTransactionResult(
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

    // A section read back with the persisted JSON options that wrote it, or null when the
    // result has none of that name.
    public TValue? ReadSection<TValue>(string name)
        where TValue : class
    {
        return Section(name)?.Deserialize<TValue>(ItemOperationJson.Options);
    }
}

public sealed record ItemTransactionError(
    string Code,
    string Message);

// Currency is the balance after the commit; results stored before it existed have none.
public sealed record ItemTransactionCharacterRevision(
    Guid CharacterId,
    long Revision,
    long CarriedWeight,
    long CarryCapacity,
    long? Currency = null);

// The final state of one item the transaction changed. A removed item has no definition,
// and an equipped item no container. Policies are the active ones, as snapshots show them.
// A Bag in the Bag slot names its contents container and its revision.
public sealed record ItemTransactionItemChange(
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

public sealed record ItemTransactionItemRevision(
    Guid ItemInstanceId,
    long Revision);

public sealed record ItemTransactionContainerRevision(
    Guid ContainerId,
    long Revision);

// An active policy of an item as inventory snapshots and item changes show it.
public sealed record ItemPolicySummaryResponse(
    string PolicyKind,
    string Status,
    string ProtectionSource);
