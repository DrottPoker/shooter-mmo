using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.WorldData.Characters;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Appearance;

// Dresses development bots, which have no stored look and no items: each bot gets a body model,
// a look, and an outfit picked from its character id by a stable hash, so a crowd shows every
// body, face, and garment and each bot keeps its own, as Victor decided on 2026-10-25. The outfit
// is only shown; the bot owns none of it.
public sealed class DevelopmentBotLooks
{
    // The equipment slots a bot always wears something in; the other visible slots may stay empty.
    private static readonly string[] AlwaysDressed = ["body", "legs", "feet"];

    private readonly CharacterAppearanceCatalog appearance;
    private readonly Dictionary<string, string[]> definitionsBySlot;

    public DevelopmentBotLooks(CharacterAppearanceCatalog appearance, ItemCatalogRuntimeDocument items)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(items);
        this.appearance = appearance;
        definitionsBySlot = appearance.VisibleEquipmentSlots.ToDictionary(
            slot => slot,
            slot => (items.Definitions ?? [])
                .Where(definition => definition.EquipmentSlots.Contains(slot, StringComparer.Ordinal))
                .Select(definition => definition.Id)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    public CharacterLook For(Guid characterId)
    {
        var seed = characterId.ToString("D");
        var bodyModel = Pick(appearance.BodyModels, seed + "/Model");
        var choices = new List<SimulationAppearanceChoice>();
        foreach (var offer in appearance.OffersFor(bodyModel))
        {
            appearance.TryGetKind(offer.Kind, out var kind);
            var option = kind!.Required
                ? Pick(offer.Options, $"{seed}/{offer.Kind}/{offer.Slot}")
                : Pick([null, .. offer.Options], $"{seed}/{offer.Kind}/{offer.Slot}");
            if (option is not null)
            {
                choices.Add(new SimulationAppearanceChoice(offer.Kind, offer.Slot, option));
            }
        }

        var outfit = new List<CharacterLookItem>();
        foreach (var (slot, definitions) in definitionsBySlot)
        {
            if (definitions.Length == 0)
            {
                continue;
            }

            var definition = AlwaysDressed.Contains(slot, StringComparer.Ordinal)
                ? Pick(definitions, $"{seed}/{slot}")
                : Pick([null, .. definitions], $"{seed}/{slot}");
            if (definition is not null)
            {
                outfit.Add(new CharacterLookItem(slot, definition));
            }
        }

        return new CharacterLook(new SimulationAppearance(bodyModel, choices, HeadwearLowered: false), outfit);
    }

    private static T Pick<T>(IReadOnlyList<T> values, string key)
    {
        return values[(int)(Mix(StableHash(key)) % (uint)values.Count)];
    }

    // FNV-1a over the UTF-16 code units and the MurmurHash3 finalizer.
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var character in text)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash;
    }

    private static uint Mix(uint hash)
    {
        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35u;
        hash ^= hash >> 16;
        return hash;
    }
}
