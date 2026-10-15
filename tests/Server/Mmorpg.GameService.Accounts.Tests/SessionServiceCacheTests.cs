using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Accounts.Tests;

public sealed class SessionServiceCacheTests
{
    [Fact]
    public async Task ResolveTokenUsesCacheWithoutOpeningPostgresConnection()
    {
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var cache = new HitSessionCache(new AccountSessionCacheEntry(
            accountId,
            "cached_player",
            sessionId,
            DateTime.UtcNow.AddMinutes(5)));
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unreachable;Username=test;Password=test;Timeout=1");
        var service = new SessionService(
            dataSource,
            new AccountsModuleOptions(
                TimeSpan.FromHours(24),
                5,
                TimeSpan.FromSeconds(60),
                new SimulationWorkerCredentials([], null)),
            new UnusedSessionLifecycleHandler(),
            cache);

        var result = await service.ResolveTokenAsync(
            "valid-looking-session-token",
            CancellationToken.None);

        Assert.NotNull(result.Account);
        Assert.Equal(accountId, result.Account.AccountId);
        Assert.Equal(sessionId, result.Account.SessionId);
        Assert.Equal(1, cache.Reads);
    }

    // Resolving a token never changes sessions, so the lifecycle SPI is never called.
    private sealed class UnusedSessionLifecycleHandler : IAccountSessionLifecycleHandler
    {
        public Task OnSessionsReplacedAsync(
            PostgresTransactionScope scope,
            AccountSessionsReplaced sessionsReplaced,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Token resolution must not replace sessions.");
        }

        public Task OnSessionRevokedAsync(
            PostgresTransactionScope scope,
            AccountSessionRevoked sessionRevoked,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Token resolution must not revoke sessions.");
        }
    }

    private sealed class HitSessionCache(AccountSessionCacheEntry entry) : IAccountSessionCache
    {
        public int Reads { get; private set; }

        public ValueTask<AccountSessionCacheLookup> GetAsync(
            string tokenHash,
            CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(new AccountSessionCacheLookup(
                AccountSessionCacheLookupStatus.Hit,
                entry));
        }

        public ValueTask TryStoreAsync(
            string tokenHash,
            AccountSessionCacheEntry cacheEntry,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> PrepareRevocationsAsync(
            IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(true);
        }
    }
}
