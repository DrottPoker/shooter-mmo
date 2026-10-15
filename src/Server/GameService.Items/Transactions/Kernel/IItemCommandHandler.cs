namespace Mmorpg.GameService.Items.Transactions;

// Executes one item command inside a transaction that the kernel owns. A handler reads
// and writes through the context and rejects through ItemTransactionContext.Reject; it
// never commits, rolls back, or opens a connection. Handlers are stateless and shared.
internal interface IItemCommandHandler<in TCommand>
    where TCommand : notnull
{
    Task ExecuteAsync(
        ItemTransactionContext context,
        TCommand command,
        CancellationToken cancellationToken);
}
