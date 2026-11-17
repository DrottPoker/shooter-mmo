using Mmorpg.GameService.Characters.Audit;
using Mmorpg.GameService.Platform.Audit;

namespace Mmorpg.GameService.Characters;

// The economy audit's checks over Characters' tables, the gold balances and their ledger. The
// module registers them for the service and the audit command; the load tool runs them itself.
public static class CharactersAudit
{
    public static IReadOnlyList<IAuditCheck> Checks => CurrencyLedgerAuditChecks.All;
}
