using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mmorpg.GameService.Placement.Contracts;

namespace Mmorpg.GameService.Items.Contracts;

// The acting character's carry state and equipment, read after the operation committed.
// The worker applies it when its item-state revision is newer than what it knows.
public sealed record SimulationCharacterStateResponse(
    Guid CharacterId,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    IReadOnlyList<SimulationEquippedItem> Equipment);

// A committed item transaction together with the acting character's state afterwards. The
// result's sections pass through as top-level keys after the other properties, as written,
// such as the questLog that names the quests a quest action or a counted gathering yield
// changed. A section the result does not hold, or holds as null, has no key.
public sealed record SimulationItemOperationResponse(
    Guid OperationId,
    string OperationKind,
    bool Succeeded,
    ItemTransactionError? Error,
    IReadOnlyList<ItemTransactionCharacterRevision> CharacterRevisions,
    IReadOnlyList<ItemTransactionContainerRevision> ContainerRevisions,
    IReadOnlyList<ItemTransactionItemRevision> ItemRevisions,
    IReadOnlyList<Guid> RecoveryDeliveryIds,
    SimulationCharacterStateResponse? CharacterState,
    IReadOnlyList<ItemTransactionItemChange>? ItemChanges)
{
    // A section never replaces a property of the response, which the worker reads without
    // regard to case.
    private static readonly FrozenSet<string> PropertyNames = typeof(SimulationItemOperationResponse)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Sections { get; init; }

    public static SimulationItemOperationResponse From(
        ItemTransactionResult result,
        SimulationCharacterStateResponse? characterState)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new SimulationItemOperationResponse(
            result.OperationId,
            result.OperationKind,
            result.Succeeded,
            result.Error,
            result.CharacterRevisions,
            result.ContainerRevisions,
            result.ItemRevisions,
            result.RecoveryDeliveryIds,
            characterState,
            result.ItemChanges)
        {
            Sections = PassedSections(result)
        };
    }

    private static Dictionary<string, JsonElement>? PassedSections(ItemTransactionResult result)
    {
        var sections = result.Sections?
            .Where(section => section.Value.ValueKind != JsonValueKind.Null
                && !PropertyNames.Contains(section.Key))
            .ToDictionary(section => section.Key, section => section.Value, StringComparer.Ordinal);
        return sections is { Count: > 0 } ? sections : null;
    }
}
