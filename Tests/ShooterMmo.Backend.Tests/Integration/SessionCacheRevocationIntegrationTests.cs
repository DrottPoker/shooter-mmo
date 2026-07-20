using AuthService.Auth;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class SessionCacheRevocationIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task FailedCacheInvalidationDoesNotCommitDurableRevocation()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var service = new SessionService(
            context.DataSource,
            context.Configuration,
            new RejectingSessionCache());

        var result = await service.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
        var stillAuthenticated = await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None);
        Assert.NotNull(stillAuthenticated);
    }

    private sealed class RejectingSessionCache : IAccountSessionCache
    {
        public ValueTask<AccountSessionCacheLookup> GetAsync(
            string tokenHash,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new AccountSessionCacheLookup(
                AccountSessionCacheLookupStatus.Miss));
        }

        public ValueTask TryStoreAsync(
            string tokenHash,
            AccountSessionCacheEntry entry,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> PrepareRevocationsAsync(
            IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(false);
        }
    }
}
