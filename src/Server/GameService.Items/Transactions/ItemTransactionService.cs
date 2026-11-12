using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items;

// The entry of every durable item command: it looks up the registration of the command's
// type in the item command catalog and runs the request through the transaction kernel. The
// modules above Items reach it as IItemTransactions.
internal sealed class ItemTransactionService(
    ItemCommandCatalog catalog,
    ItemTransactionKernel kernel) : IItemTransactions
{
    // Throws InvalidOperationException for a command type without a registration.
    public Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        return kernel.ExecuteAsync(request, catalog.Get<TCommand>(), cancellationToken);
    }

    // The presentation key of every player corpse, also used by corpse tooling and fixtures.
    internal static string DefaultPlayerCorpsePresentationKey =>
        CorpseTransformRules.PlayerCorpsePresentationKey;
}
