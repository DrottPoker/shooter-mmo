namespace Mmorpg.GameService.Items.Contracts;

// The entry of every durable item command for the modules above Items: it runs the request
// through the item transaction kernel with the registration of the command's type. It is
// scoped, like the item transaction service that implements it.
public interface IItemTransactions
{
    // Throws InvalidOperationException for a command type without a registration.
    Task<ItemTransactionResult> ExecuteAsync<TCommand>(
        ItemTransactionRequest<TCommand> request,
        CancellationToken cancellationToken)
        where TCommand : notnull;
}

public sealed record ItemTransactionRequest<TCommand>(
    Guid OperationId,
    ItemTransactionActor Actor,
    TCommand Command)
    where TCommand : notnull;
