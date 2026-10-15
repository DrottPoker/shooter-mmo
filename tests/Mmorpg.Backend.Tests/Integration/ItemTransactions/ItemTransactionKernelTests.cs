using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.Backend.Tests.Unit;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement.Contracts;
using Npgsql;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// White-box tests of the transaction kernel with test handlers: how it maps, stores,
// releases, or propagates each kind of handler failure.
public sealed class ItemTransactionKernelTests
{
    [PostgresIntegrationFact]
    public async Task UnmappedDatabaseErrorPropagatesRollsBackAndLogsError()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var logger = new StructuredLogger<ItemTransactionService>();
        var kernel = CreateKernel(context, logger);
        var request = Request();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            null,
            new TestHandler(async (transactionContext, _) =>
            {
                await transactionContext.Connection.ExecuteAsync(new CommandDefinition(
                    "select * from missing_kernel_test_table;",
                    transaction: transactionContext.Transaction));
            }),
            CancellationToken.None));

        Assert.Equal("42P01", exception.SqlState);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Null(await LoadStatusAsync(context, request.OperationId));
    }

    [PostgresIntegrationFact]
    public async Task HandlerWithoutAuditChangesFailsAndStoresNothing()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var kernel = CreateKernel(context);
        var request = Request();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.ExecuteAsync(
            request,
            ItemOperationKinds.Grant,
            null,
            new TestHandler((_, _) => Task.CompletedTask),
            CancellationToken.None));

        Assert.Equal("Item operation 'grant' produced no audit changes.", exception.Message);
        Assert.Null(await LoadStatusAsync(context, request.OperationId));
    }

    [PostgresIntegrationFact]
    public async Task MappedDatabaseErrorsAreReleasedWhenTransientAndStoredOtherwise()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var kernel = CreateKernel(context);

        var deadlock = Request();
        var deadlockResult = await kernel.ExecuteAsync(
            deadlock,
            ItemOperationKinds.Grant,
            null,
            new TestHandler((_, _) => throw ItemTransactionOutcomesTests.CreateException("40P01", null)),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, deadlockResult.Error!.Code);
        Assert.Null(await LoadStatusAsync(context, deadlock.OperationId));

        var quantity = Request();
        var quantityResult = await kernel.ExecuteAsync(
            quantity,
            ItemOperationKinds.Grant,
            null,
            new TestHandler((_, _) => throw ItemTransactionOutcomesTests.CreateException(
                "23514",
                "ck_item_instances_quantity")),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemQuantityChanged, quantityResult.Error!.Code);
        Assert.Equal("rejected", await LoadStatusAsync(context, quantity.OperationId));
    }

    [PostgresIntegrationFact]
    public async Task RetriedSystemRejectionReleasesTheClaimAndOtherRejectionsAreStored()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var kernel = CreateKernel(context);
        var rejectingHandler = new TestHandler((_, _) =>
        {
            ItemTransactionContext.Reject(ItemTransactionErrorCodes.ItemNotFound, "Rejected by the test.");
            return Task.CompletedTask;
        });

        var retried = Request();
        var retriedResult = await kernel.ExecuteAsync(
            retried,
            ItemOperationKinds.ExpireCorpse,
            null,
            rejectingHandler,
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, retriedResult.Error!.Code);
        Assert.Equal(ItemOperationKinds.ExpireCorpse, retriedResult.OperationKind);
        Assert.Null(await LoadStatusAsync(context, retried.OperationId));

        var stored = Request();
        var storedResult = await kernel.ExecuteAsync(
            stored,
            ItemOperationKinds.Grant,
            null,
            rejectingHandler,
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, storedResult.Error!.Code);
        Assert.Equal("rejected", await LoadStatusAsync(context, stored.OperationId));
    }

    internal static ItemTransactionKernel CreateKernel(
        PostgresIntegrationTestContext context,
        StructuredLogger<ItemTransactionService>? logger = null,
        ItemOperationsOptions? options = null,
        NpgsqlDataSource? dataSource = null)
    {
        var services = context.Services;
        var source = dataSource ?? context.DataSource;
        return new ItemTransactionKernel(
            source,
            options ?? ItemOperationsOptions.CreateDefaults(),
            null,
            new ItemTransactionTelemetry(null, logger, null),
            new ItemOperationLedger(source),
            new ItemSimulationAuthority(
                services.GetRequiredService<ICharacterLocks>(),
                services.GetRequiredService<ISimulationSessionAuthority>(),
                services.GetRequiredService<ISimulationWorkerAuthority>()),
            new CharacterItemStateLocks(
                services.GetRequiredService<ICharacterLocks>(),
                services.GetRequiredService<ISimulationPresence>(),
                null));
    }

    internal static ItemTransactionRequest<GrantItemCommand> Request()
    {
        return new ItemTransactionRequest<GrantItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            new GrantItemCommand(Guid.NewGuid(), null, "material.iron_ore", 1, Guid.NewGuid(), 0));
    }

    internal static async Task<string?> LoadStatusAsync(
        PostgresIntegrationTestContext context,
        Guid operationId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "select status from item_operations where operation_id = @OperationId;",
            new { OperationId = operationId });
    }

    // A handler for kernel tests; it receives the context and the command.
    internal sealed class TestHandler(Func<ItemTransactionContext, GrantItemCommand, Task> execute)
        : IItemCommandHandler<GrantItemCommand>
    {
        public Task ExecuteAsync(
            ItemTransactionContext context,
            GrantItemCommand command,
            CancellationToken cancellationToken)
        {
            return execute(context, command);
        }
    }
}
