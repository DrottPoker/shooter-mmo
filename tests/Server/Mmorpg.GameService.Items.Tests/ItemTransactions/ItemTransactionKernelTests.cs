using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

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
            Registration(ItemOperationKinds.Grant, new TestHandler(async (transactionContext, _) =>
            {
                await transactionContext.Connection.ExecuteAsync(new CommandDefinition(
                    "select * from missing_kernel_test_table;",
                    transaction: transactionContext.Transaction));
            })),
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
            Registration(ItemOperationKinds.Grant, new TestHandler((_, _) => Task.CompletedTask)),
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
            Registration(
                ItemOperationKinds.Grant,
                new TestHandler((_, _) => throw ItemTransactionOutcomesTests.CreateException("40P01", null))),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemTransactionTimeout, deadlockResult.Error!.Code);
        Assert.Null(await LoadStatusAsync(context, deadlock.OperationId));

        var quantity = Request();
        var quantityResult = await kernel.ExecuteAsync(
            quantity,
            Registration(
                ItemOperationKinds.Grant,
                new TestHandler((_, _) => throw ItemTransactionOutcomesTests.CreateException(
                    "23514",
                    "ck_item_instances_quantity"))),
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
            Registration(ItemOperationKinds.ExpireCorpse, rejectingHandler, retriedSystemOperation: true),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, retriedResult.Error!.Code);
        Assert.Equal(ItemOperationKinds.ExpireCorpse, retriedResult.OperationKind);
        Assert.Null(await LoadStatusAsync(context, retried.OperationId));

        var stored = Request();
        var storedResult = await kernel.ExecuteAsync(
            stored,
            Registration(ItemOperationKinds.Grant, rejectingHandler),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemNotFound, storedResult.Error!.Code);
        Assert.Equal("rejected", await LoadStatusAsync(context, stored.OperationId));
    }

    internal static ItemTransactionKernel CreateKernel(
        PostgresIntegrationTestContext context,
        StructuredLogger<ItemTransactionService>? logger = null,
        ItemOperationsOptions? options = null,
        NpgsqlDataSource? dataSource = null,
        NpgsqlDataSource? ledgerDataSource = null)
    {
        var services = context.Services;
        var source = dataSource ?? context.DataSource;
        return new ItemTransactionKernel(
            source,
            new ItemTransactionTelemetry(null, logger, null),
            new ItemOperationLedger(ledgerDataSource ?? source),
            new ItemSimulationAuthority(
                services.GetRequiredService<ICharacterLocks>(),
                services.GetRequiredService<ISimulationSessionAuthority>(),
                services.GetRequiredService<ISimulationWorkerAuthority>()),
            new CharacterItemStateLocks(
                services.GetRequiredService<ICharacterLocks>(),
                services.GetRequiredService<ISimulationPresence>(),
                null),
            options ?? ItemOperationsOptions.CreateDefaults(),
            null);
    }

    // Registers a test handler under an operation kind. The kernel takes the retried system
    // operation flag from the registration, so a test that needs it sets it.
    internal static ItemCommandRegistration<GrantItemCommand> Registration(
        string operationKind,
        TestHandler handler,
        Func<GrantItemCommand, Guid?>? actorCharacter = null,
        bool retriedSystemOperation = false)
    {
        return new ItemCommandRegistration<GrantItemCommand>(
            operationKind,
            actorCharacter ?? (static _ => null),
            handler)
        {
            RetriedSystemOperation = retriedSystemOperation
        };
    }

    internal static ItemTransactionRequest<GrantItemCommand> Request()
    {
        return new ItemTransactionRequest<GrantItemCommand>(
            Guid.NewGuid(),
            ItemTransactionActor.ForSystem(),
            new GrantItemCommand(Guid.NewGuid(), null, "fixture.ore", 1, Guid.NewGuid(), 0));
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
