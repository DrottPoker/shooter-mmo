using System.Text;

namespace Mmorpg.GameService.Items.Transactions;

// Admission checks that run before any database access. A request that fails them is
// rejected without being stored.
internal static class ItemOperationAdmission
{
    public static bool RequiresRandomClientOperationId(
        ItemTransactionActor actor,
        ItemCommandRegistration registration)
    {
        return actor.Authority == ItemTransactionAuthority.Account
            || (actor.Authority == ItemTransactionAuthority.SimulationWorker
                && !registration.RetriedSystemOperation);
    }

    public static bool ExceedsPayloadLimit(
        string requestPayload,
        ItemOperationsOptions options)
    {
        return Encoding.UTF8.GetByteCount(requestPayload)
            > options.MaximumCommandPayloadBytes;
    }
}
