namespace Mmorpg.GameService.Accounts;

internal enum AccountSessionCacheLookupStatus
{
    Hit,
    Miss,
    TombstoneBypass,
    Unavailable
}

internal sealed record AccountSessionCacheEntry(
    Guid AccountId,
    string Username,
    Guid SessionId,
    DateTime ExpiresAt);

internal sealed record AccountSessionCacheLookup(
    AccountSessionCacheLookupStatus Status,
    AccountSessionCacheEntry? Entry = null);

internal sealed record AccountSessionCacheRevocation(
    Guid SessionId,
    string TokenHash,
    string Reason);

internal interface IAccountSessionCache
{
    ValueTask<AccountSessionCacheLookup> GetAsync(
        string tokenHash,
        CancellationToken cancellationToken);

    ValueTask TryStoreAsync(
        string tokenHash,
        AccountSessionCacheEntry entry,
        CancellationToken cancellationToken);

    ValueTask<bool> PrepareRevocationsAsync(
        IReadOnlyCollection<AccountSessionCacheRevocation> revocations,
        CancellationToken cancellationToken);
}

internal sealed class NullAccountSessionCache : IAccountSessionCache
{
    public static NullAccountSessionCache Instance { get; } = new();

    private NullAccountSessionCache()
    {
    }

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
        return ValueTask.FromResult(true);
    }
}
