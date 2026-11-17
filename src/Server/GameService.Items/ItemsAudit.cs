using Mmorpg.GameService.Items.Audit;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Items;

// The economy audit's checks over Items' tables: item custody, slots, quantities, definitions,
// and what the retained history says about destruction and conservation. The module registers
// them for the service and the audit command; the load tool runs them itself.
public static class ItemsAudit
{
    public static IReadOnlyList<IAuditCheck> Checks { get; } =
        [.. ItemCustodyAuditChecks.All, .. ItemHistoryAuditChecks.All];
}
