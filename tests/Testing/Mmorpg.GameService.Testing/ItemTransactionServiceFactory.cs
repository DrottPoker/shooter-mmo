using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Testing;

// Builds an ItemTransactionService on a module service graph with a test's own data source,
// options, metrics, logger, or HTTP context accessor. Every part a test does not replace,
// including the command catalog, comes from the graph.
internal static class ItemTransactionServiceFactory
{
    public static ItemTransactionService Create(
        IServiceProvider services,
        NpgsqlDataSource? dataSource = null,
        ItemOperationsOptions? options = null,
        ItemOperationsMetrics? metrics = null,
        ILogger<ItemTransactionService>? logger = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        var source = dataSource ?? services.GetRequiredService<NpgsqlDataSource>();
        var operationsMetrics = metrics ?? services.GetService<ItemOperationsMetrics>();
        var kernel = new ItemTransactionKernel(
            source,
            new ItemTransactionTelemetry(
                operationsMetrics,
                logger ?? services.GetService<ILogger<ItemTransactionService>>(),
                httpContextAccessor ?? services.GetService<IHttpContextAccessor>()),
            new ItemOperationLedger(source),
            services.GetRequiredService<ItemSimulationAuthority>(),
            new CharacterItemStateLocks(
                services.GetRequiredService<ICharacterLocks>(),
                services.GetRequiredService<ISimulationPresence>(),
                operationsMetrics),
            options ?? services.GetService<ItemOperationsOptions>(),
            operationsMetrics);
        return new ItemTransactionService(services.GetRequiredService<ItemCommandCatalog>(), kernel);
    }
}
