using Mmorpg.Tools.WorldActorCompiler;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// The compiled item and actor catalogs, which a Unity client also reads: equipment slots,
// weapon ranges, tool kinds, vendor offers and prices, the tool a resource node
// needs, the interaction bounds of an actor, and how far an aggressive Mob detects a player.
public sealed class PlayingGameData
{
    private readonly Dictionary<string, ItemDefinition> items;
    private readonly Dictionary<string, VendorProfileDefinition> vendors;
    private readonly Dictionary<string, WorldActorDefinition> actors;
    private readonly Dictionary<string, WorldActorResourceProfileDefinition> resourceProfiles;
    private readonly Dictionary<string, WorldActorBrainProfileDefinition> brainProfiles;

    public PlayingGameData(ItemCatalogRuntimeDocument itemCatalog, WorldActorCatalogRuntimeDocument actorCatalog)
    {
        ArgumentNullException.ThrowIfNull(itemCatalog);
        ArgumentNullException.ThrowIfNull(actorCatalog);
        ItemCatalog = itemCatalog;
        items = (itemCatalog.Definitions ?? []).ToDictionary(item => item.Id, StringComparer.Ordinal);
        vendors = (itemCatalog.Vendors ?? []).ToDictionary(vendor => vendor.Id, StringComparer.Ordinal);
        actors = (actorCatalog.Actors ?? []).ToDictionary(actor => actor.Id, StringComparer.Ordinal);
        resourceProfiles = (actorCatalog.ResourceProfiles ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        brainProfiles = (actorCatalog.BrainProfiles ?? []).ToDictionary(profile => profile.Id, StringComparer.Ordinal);
    }

    public ItemCatalogRuntimeDocument ItemCatalog { get; }

    public static PlayingGameData Load(string itemCatalogPath, string actorCatalogPath)
    {
        return new PlayingGameData(
            PlayingRoster.LoadItemCatalog(itemCatalogPath),
            WorldActorJson.DeserializeCatalogRuntime(File.ReadAllText(actorCatalogPath)));
    }

    public ItemDefinition? Item(string definitionId)
    {
        return items.GetValueOrDefault(definitionId);
    }

    public VendorProfileDefinition? Vendor(string profileId)
    {
        return vendors.GetValueOrDefault(profileId);
    }

    public bool HasToolKind(string itemDefinitionId, string toolKind)
    {
        return items.TryGetValue(itemDefinitionId, out var item)
            && (item.ToolKinds ?? []).Contains(toolKind, StringComparer.Ordinal);
    }

    // The tool kind a resource node's gathering capability needs, or null for no node.
    public string? RequiredToolKind(string actorDefinitionId)
    {
        if (!actors.TryGetValue(actorDefinitionId, out var actor))
        {
            return null;
        }

        foreach (var capability in actor.Capabilities ?? [])
        {
            if (capability.Kind == WorldActorCapabilityKindIds.Gathering
                && resourceProfiles.TryGetValue(capability.ProfileId ?? string.Empty, out var profile))
            {
                return profile.RequiredToolKind;
            }
        }

        return null;
    }

    // The box around an actor that interaction range and line of sight measure against, relative
    // to the actor's position.
    public WorldActorBoundsDefinition? InteractionBounds(string actorDefinitionId)
    {
        return actors.TryGetValue(actorDefinitionId, out var actor) ? actor.InteractionBounds : null;
    }

    // How far an aggressive Mob detects a player, or 0 for a Mob that only fights back.
    public float AggroRange(string actorDefinitionId)
    {
        return actors.TryGetValue(actorDefinitionId, out var actor)
            && brainProfiles.TryGetValue(actor.BrainProfileId ?? string.Empty, out var brain)
            && brain.Aggression == "aggressive"
                ? brain.DetectionRange
                : 0f;
    }

    // The total a vendor asks for a quantity of an offer, or null when it does not sell it.
    public long? PurchasePrice(VendorProfileDefinition vendor, string definitionId, int quantity)
    {
        return items.TryGetValue(definitionId, out var item)
            && VendorPricing.Offers(vendor, definitionId)
            && VendorPricing.TryGetPurchasePrice(item, vendor, quantity, out var price)
                ? price
                : null;
    }

    // The total a vendor pays for a quantity, or null when it buys nothing of the kind.
    public long? SalePrice(VendorProfileDefinition vendor, string definitionId, int quantity)
    {
        return items.TryGetValue(definitionId, out var item)
            && VendorPricing.AcceptsSale(vendor, item)
            && VendorPricing.TryGetSalePrice(item, vendor, quantity, out var price)
            && price > 0
                ? price
                : null;
    }
}
