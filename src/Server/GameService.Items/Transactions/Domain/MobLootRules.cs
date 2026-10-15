using System.Security.Cryptography;
using System.Text;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Mob loot rules and the deterministic ids derived from Mob corpse and grant ids.
// Derived ids are persisted, so CreateScopedGuid must never change.
internal static class MobLootRules
{
    public static void ValidateMobLootDefinition(
        ResolvedItemDefinition definition,
        int quantity)
    {
        if (quantity <= 0
            || quantity > definition.RuntimeDefinition.MaximumStackSize
            || definition.IsBag
            || definition.RuntimeDefinition.DefaultPolicies.Length > 0)
        {
            ItemTransactionContext.Reject(
                ItemTransactionErrorCodes.MobLootGrantInvalid,
                "Mob loot must be transferable, policy-free, non-Bag item content within its stack limit.");
        }
    }

    public static bool IsMobIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    public static Guid CreateScopedGuid(string scope, Guid sourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            scope + ":" + sourceId.ToString("N")));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
