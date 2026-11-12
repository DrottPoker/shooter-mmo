using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests.ItemTransactions;

// The production service graph on a data source that never connects, for tests that need the
// module's item command registrations without PostgreSQL.
internal sealed class OfflineItemComposition : IAsyncDisposable
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1";

    private readonly NpgsqlDataSource dataSource;
    private readonly ServiceProvider provider;

    public OfflineItemComposition()
    {
        dataSource = NpgsqlDataSource.Create(UnreachableDatabase);
        try
        {
            provider = GameServiceTestComposition.BuildServiceProvider(dataSource);
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    public IServiceProvider Services => provider;

    public ItemCommandCatalog Catalog => provider.GetRequiredService<ItemCommandCatalog>();

    public ItemCommandRegistration Registration(string operationKind)
    {
        return Assert.Single(
            Catalog.Registrations,
            registration => string.Equals(registration.OperationKind, operationKind, StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        await dataSource.DisposeAsync();
    }
}
