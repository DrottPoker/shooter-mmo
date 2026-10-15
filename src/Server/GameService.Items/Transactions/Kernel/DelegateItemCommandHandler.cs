namespace Mmorpg.GameService.Items.Transactions;

// Adapts a handler method to IItemCommandHandler until every command family has its own
// handler class.
internal sealed class DelegateItemCommandHandler<TCommand>(
    Func<ItemTransactionContext, TCommand, CancellationToken, Task> execute)
    : IItemCommandHandler<TCommand>
    where TCommand : notnull
{
    public Task ExecuteAsync(
        ItemTransactionContext context,
        TCommand command,
        CancellationToken cancellationToken)
    {
        return execute(context, command, cancellationToken);
    }
}
