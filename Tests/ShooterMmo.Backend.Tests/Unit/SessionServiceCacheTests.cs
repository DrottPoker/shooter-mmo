using AuthService.Auth;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Unit;

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
            new ConfigurationBuilder().Build(),
            cache);

        var result = await service.ResolveTokenAsync(
            "valid-looking-session-token",
            CancellationToken.None);

        Assert.NotNull(result.Account);
        Assert.Equal(accountId, result.Account.AccountId);
        Assert.Equal(sessionId, result.Account.SessionId);
        Assert.Equal(1, cache.Reads);
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
