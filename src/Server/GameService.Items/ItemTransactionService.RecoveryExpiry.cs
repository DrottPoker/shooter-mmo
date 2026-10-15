using Dapper;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items;

internal sealed partial class ItemTransactionService
{
    public Task<ItemTransactionResult> ExecuteAsync(
        ItemTransactionRequest<ExpireRecoveryDeliveryCommand> request,
        CancellationToken cancellationToken)
    {
        return kernel.ExecuteAsync(
            request,
            ItemOperationKinds.ExpireRecoveryDelivery,
            null,
            recoveryExpiry,
            cancellationToken);
    }
}
