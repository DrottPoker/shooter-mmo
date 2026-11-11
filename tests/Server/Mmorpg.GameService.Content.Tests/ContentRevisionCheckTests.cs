using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameService.Content;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Content.Tests;

public sealed class ContentRevisionCheckTests
{
    // A database that cannot be read is a matter for readiness: the check logs a warning and
    // lets the service start, as it started before the check existed.
    [Fact]
    public async Task ADatabaseThatCannotBeReachedDoesNotStopTheStart()
    {
        // Nothing listens on port 1, so the connection is refused.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=2;Pooling=false");
        var revisions = new UnreadRevisions();
        var check = new ContentRevisionCheck(
            ContentFileLoader.Load(GameServiceTestComposition.CreateContentOptions()),
            dataSource,
            revisions,
            revisions,
            NullLogger<ContentRevisionCheck>.Instance);

        await check.StartAsync(CancellationToken.None);

        Assert.False(revisions.WasRead);
    }

    private sealed class UnreadRevisions : IItemCatalogRevisionProvider, IQuestCatalogRevisionProvider
    {
        public bool WasRead { get; private set; }

        public Task<string?> GetCurrentRevisionAsync(
            PostgresTransactionScope scope,
            CancellationToken cancellationToken)
        {
            WasRead = true;
            return Task.FromResult<string?>(null);
        }
    }
}
