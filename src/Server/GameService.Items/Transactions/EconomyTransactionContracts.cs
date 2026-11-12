using System.Text.Json.Serialization;
using Mmorpg.GameService.Items.Transactions;

namespace Mmorpg.GameService.Items;

// The vendor and gathering commands with their operation kinds, and the vendor capability that
// the worker claims for a trade.

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
// ResourceActorId is the actor definition of the gathered node, whose yields count for gather
// objectives; a command without it counts for none.
internal sealed record GatherResourceCommand(
    Guid CharacterId,
    string ResourceProfileId,
    string ResourceNodeId,
    string RequiredToolKind,
    string YieldDefinitionId,
    int YieldQuantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceActorId = null);

internal static partial class ItemOperationKinds
{
    public const string VendorPurchase = "vendor_purchase";
    public const string VendorSale = "vendor_sale";
    public const string GatherResource = "gather_resource";
}

// Purchases and sales need a live interaction with an NPC whose vendor capability names exactly
// the trade's vendor profile, which the worker claims for the operation.
internal static class VendorAccess
{
    public const string CapabilityKind = "vendor";

    public static ItemTransactionLiveAccess Claim(ItemTransactionLiveAccess access, string? vendorProfileId)
    {
        ArgumentNullException.ThrowIfNull(access);
        return vendorProfileId is null ? access : access.WithCapability(CapabilityKind, vendorProfileId);
    }

    public static void Ensure(ItemActorAuthority authority, string vendorId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        authority.EnsureCapability(
            CapabilityKind,
            vendorId,
            ItemTransactionErrorCodes.VendorAccessRequired,
            "This operation requires a validated interaction with the vendor.");
    }
}
