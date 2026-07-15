using System.Text.Json;
using System.Text.Json.Serialization;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public static class ItemCatalogRuntimeLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<ItemCatalogRuntimeDocument> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The compiled item catalog required by AuthService does not exist.",
                path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The compiled item catalog is empty.");
        }

        ItemCatalogRuntimeDocument runtime;
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
            RejectDuplicateProperties(document.RootElement, "$");
            runtime = JsonSerializer.Deserialize<ItemCatalogRuntimeDocument>(json, SerializerOptions)
                ?? throw new InvalidDataException("The compiled item catalog has no root object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The compiled item catalog is malformed: {exception.Message}",
                exception);
        }

        return ValidateAndCanonicalize(runtime);
    }

    public static ItemCatalogRuntimeDocument ValidateAndCanonicalize(
        ItemCatalogRuntimeDocument runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var authoring = new ItemCatalogAuthoringDocument
        {
            FormatVersion = runtime.FormatVersion,
            CatalogId = runtime.CatalogId,
            BaseSecureContainerTierId = runtime.BaseSecureContainerTierId,
            Categories = CloneIdentities(runtime.Categories),
            Tags = CloneIdentities(runtime.Tags),
            EquipmentSlots = CloneIdentities(runtime.EquipmentSlots),
            Definitions = runtime.Definitions?
                .Select(ToAuthoringDefinition)
                .ToArray(),
            SecureContainerTiers = runtime.SecureContainerTiers?
                .Select(tier => new SecureContainerTierAuthoringEntry
                {
                    Id = tier?.Id,
                    DisplayName = tier?.DisplayName,
                    SlotCapacity = tier?.SlotCapacity ?? 0
                })
                .ToArray()
        };

        var canonical = ItemCatalogCompiler.Compile(authoring);
        if (!string.Equals(runtime.Revision, canonical.Revision, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The compiled item catalog revision does not match its gameplay content.");
        }

        var runtimeDefinitions = runtime.Definitions?
            .Where(definition => definition is not null)
            .ToDictionary(definition => definition.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
        foreach (var definition in canonical.Definitions)
        {
            if (!runtimeDefinitions.TryGetValue(definition.Id, out var runtimeDefinition)
                || !string.Equals(
                    runtimeDefinition.StructuralFingerprint,
                    definition.StructuralFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Item definition '{definition.Id}' has a stale structural fingerprint.");
            }
        }

        var runtimeTiers = runtime.SecureContainerTiers?
            .Where(tier => tier is not null)
            .ToDictionary(tier => tier.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, SecureContainerTierDefinition>(StringComparer.Ordinal);
        foreach (var tier in canonical.SecureContainerTiers)
        {
            if (!runtimeTiers.TryGetValue(tier.Id, out var runtimeTier)
                || !string.Equals(
                    runtimeTier.StructuralFingerprint,
                    tier.StructuralFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Secure Container tier '{tier.Id}' has a stale structural fingerprint.");
            }
        }

        return canonical;
    }

    private static ItemCatalogIdentityEntry[]? CloneIdentities(
        ItemCatalogIdentityEntry[]? entries)
    {
        return entries?
            .Select(entry => entry is null
                ? null!
                : new ItemCatalogIdentityEntry
                {
                    Id = entry.Id,
                    DisplayName = entry.DisplayName
                })
            .ToArray();
    }

    private static ItemDefinitionAuthoringEntry ToAuthoringDefinition(ItemDefinition? definition)
    {
        if (definition is null)
        {
            return null!;
        }

        return new ItemDefinitionAuthoringEntry
        {
            Id = definition.Id,
            DisplayName = definition.DisplayName,
            Category = definition.Category,
            Tags = definition.Tags,
            UnitWeight = definition.UnitWeight,
            MaximumStackSize = definition.MaximumStackSize,
            EquipmentSlots = definition.EquipmentSlots,
            PlayerDestroyable = definition.PlayerDestroyable,
            LocationEligibility = definition.LocationEligibility is null
                ? null!
                : new ItemLocationEligibilityAuthoringEntry
                {
                    SecureContainer = definition.LocationEligibility.SecureContainer
                },
            DefaultPolicies = definition.DefaultPolicies,
            Bag = definition.Bag is null
                ? null!
                : new BagDefinitionAuthoringEntry
                {
                    CarryCapacityBonus = definition.Bag.CarryCapacityBonus,
                    Slots = definition.Bag.Slots?
                        .Select(slot => slot is null
                            ? null!
                            : new BagSlotAuthoringEntry
                            {
                                Index = slot.Index,
                                Kind = slot.Kind,
                                AcceptedTags = slot.AcceptedTags
                            })
                        .ToArray()
                }
        };
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"The compiled item catalog contains duplicate property "
                        + $"'{property.Name}' at {path}.");
                }

                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            RejectDuplicateProperties(item, $"{path}[{index}]");
            index++;
        }
    }
}
