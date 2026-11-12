using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;

namespace Mmorpg.GameService.Items;

// The entry of every durable item command: it looks up the registration of the command's
// type in the item command catalog and runs the request through the transaction kernel.
internal sealed class ItemTransactionService(
    ItemCommandCatalog catalog,
    ItemTransactionKernel kernel)
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
