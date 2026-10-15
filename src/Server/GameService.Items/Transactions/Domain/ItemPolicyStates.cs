using System.Security.Cryptography;
using System.Text;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Transactions.Domain;

// Item policy rows as rule inputs, and the policy fingerprint that decides whether
// two stacks share the same policy state.
internal static class ItemPolicyStates
{
    public const string EmptyStackStateFingerprint = "item-stack-state-v1";

    public static IEnumerable<ItemPolicyState> ToPolicyStates(
        IEnumerable<LockedItemPolicy> policies)
    {
        return policies.Select(policy => new ItemPolicyState(
            policy.PolicyKind,
            policy.Status,
            policy.SourceKind,
            policy.SourceId));
    }

    public static string CalculatePolicyFingerprint(IEnumerable<LockedItemPolicy> policies)
    {
        var canonical = string.Join(
            '\n',
            policies
                .OrderBy(policy => policy.PolicyKind, StringComparer.Ordinal)
                .ThenBy(policy => policy.Status, StringComparer.Ordinal)
                .ThenBy(policy => policy.SourceKind, StringComparer.Ordinal)
                .ThenBy(policy => policy.SourceId, StringComparer.Ordinal)
                .Select(policy => string.Join(
                    '|',
                    policy.PolicyKind,
                    policy.Status,
                    policy.SourceKind,
                    policy.SourceId)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
