using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Accounts.Tests;

// An account session ends at the earlier of its last activity plus the idle lifetime, 14 days, and
// its login plus the maximum lifetime, 90 days. A token that reaches PostgreSQL is activity, which
// moves the end at most once an hour, and a session that ended never comes back. The test
// composition runs without the session cache, so every resolve reaches PostgreSQL.
public sealed class AccountSessionLifetimeIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ANewSessionEndsAfterTheIdleLifetime()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var row = await ReadAsync(context, player.Registration.SessionId);

        Assert.Equal(TimeSpan.FromDays(14), row.ExpiresAt - row.CreatedAt);
        Assert.Equal(row.ExpiresAt, player.Registration.ExpiresAt);
    }

    [PostgresIntegrationFact]
    public async Task ActivityMovesTheIdleEndAndNothingElseDoes()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var active = await context.RegisterPlayerAsync("active@example.com", "active_player", "Active Hero");
        var idle = await context.RegisterPlayerAsync("idle@example.com", "idle_player", "Idle Hero");
        await context.ExecuteAsync("update account_sessions set expires_at = now() + interval '1 day';");
        var idleEnd = (await ReadAsync(context, idle.Registration.SessionId)).ExpiresAt;

        Assert.NotNull(await context.SessionService.AuthenticateTokenAsync(
            active.Registration.SessionToken,
            CancellationToken.None));

        Assert.InRange(await IdleShortfallMinutesAsync(context, active.Registration.SessionId), -1, 1);
        Assert.Equal(idleEnd, (await ReadAsync(context, idle.Registration.SessionId)).ExpiresAt);
    }

    // Each resolve reaches PostgreSQL, but the end is written only when it moves by more than an
    // hour, so a session in use costs at most one write an hour.
    [PostgresIntegrationFact]
    public async Task ActivityWritesTheEndAtMostOnceAnHour()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var writes = new WriteCounter(context, player.Registration.SessionId);
        await writes.SetAsync("expires_at = expires_at");

        for (var index = 0; index < 10; index++)
        {
            await ResolveAsync(context, player);
            await writes.CountAsync();
        }

        Assert.Equal(0, writes.Count);

        await writes.SetAsync("expires_at = now() + interval '14 days' - interval '59 minutes'");
        for (var index = 0; index < 3; index++)
        {
            await ResolveAsync(context, player);
            await writes.CountAsync();
        }

        Assert.Equal(0, writes.Count);

        await writes.SetAsync("expires_at = now() + interval '14 days' - interval '61 minutes'");
        for (var index = 0; index < 3; index++)
        {
            await ResolveAsync(context, player);
            await writes.CountAsync();
        }

        Assert.Equal(1, writes.Count);
        Assert.InRange(await IdleShortfallMinutesAsync(context, player.Registration.SessionId), -1, 1);
    }

    // Activity never moves the end past the login plus the maximum lifetime, and once that end
    // passed, the session ended, whatever its activity.
    [PostgresIntegrationFact]
    public async Task TheMaximumLifetimeEndsASessionWhateverItsActivity()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await context.ExecuteAsync(
            $"""
            update account_sessions
            set created_at = now() - interval '89 days',
                expires_at = now() + interval '2 hours'
            where id = '{player.Registration.SessionId}';
            """);

        await ResolveAsync(context, player);
        var nearTheMaximum = await ReadAsync(context, player.Registration.SessionId);

        Assert.Equal(TimeSpan.FromDays(90), nearTheMaximum.ExpiresAt - nearTheMaximum.CreatedAt);

        await context.ExecuteAsync(
            $"""
            update account_sessions
            set created_at = now() - interval '91 days',
                expires_at = now() - interval '1 day'
            where id = '{player.Registration.SessionId}';
            """);
        var ended = await ReadAsync(context, player.Registration.SessionId);

        Assert.Equal(
            "invalid_session_token",
            (await context.SessionService.ResolveTokenAsync(player.Registration.SessionToken, CancellationToken.None)).ErrorCode);
        Assert.Equal(ended, await ReadAsync(context, player.Registration.SessionId));
    }

    // A session past its idle end, or one that a logout ended, is never extended again, so an
    // extension never revives a session that retention may already count as ended.
    [PostgresIntegrationFact]
    public async Task AnEndedSessionIsNeverExtended()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var idle = await context.RegisterPlayerAsync("idle@example.com", "idle_player", "Idle Hero");
        var loggedOut = await context.RegisterPlayerAsync("gone@example.com", "gone_player", "Gone Hero");
        await context.ExecuteAsync(
            $"update account_sessions set expires_at = now() - interval '1 minute' where id = '{idle.Registration.SessionId}';");
        var logout = await context.SessionService.RevokeAsync(
            loggedOut.Registration.AccountId,
            loggedOut.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);
        Assert.True(logout.Succeeded, logout.Error?.Message);
        await context.ExecuteAsync(
            $"update account_sessions set expires_at = now() + interval '1 day' where id = '{loggedOut.Registration.SessionId}';");
        var idleRow = await ReadAsync(context, idle.Registration.SessionId);
        var loggedOutRow = await ReadAsync(context, loggedOut.Registration.SessionId);

        Assert.Equal(
            "invalid_session_token",
            (await context.SessionService.ResolveTokenAsync(idle.Registration.SessionToken, CancellationToken.None)).ErrorCode);
        Assert.Equal(
            "invalid_session_token",
            (await context.SessionService.ResolveTokenAsync(loggedOut.Registration.SessionToken, CancellationToken.None)).ErrorCode);

        Assert.Equal(idleRow, await ReadAsync(context, idle.Registration.SessionId));
        Assert.Equal(loggedOutRow, await ReadAsync(context, loggedOut.Registration.SessionId));
    }

    private static async Task ResolveAsync(PostgresIntegrationTestContext context, IntegrationPlayer player)
    {
        Assert.NotNull(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None));
    }

    // How many minutes the stored end lies short of the idle lifetime from now.
    private static Task<int> IdleShortfallMinutesAsync(PostgresIntegrationTestContext context, Guid sessionId)
    {
        return context.ExecuteScalarIntAsync(
            $"""
            select round(extract(epoch from now() + interval '14 days' - expires_at) / 60)::integer
            from account_sessions
            where id = '{sessionId}';
            """);
    }

    private static async Task<SessionRow> ReadAsync(PostgresIntegrationTestContext context, Guid sessionId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<SessionRow>(
            """
            select created_at as "CreatedAt", expires_at as "ExpiresAt"
            from account_sessions
            where id = @SessionId;
            """,
            new { SessionId = sessionId });
    }

    private sealed record SessionRow(DateTime CreatedAt, DateTime ExpiresAt);

    // Counts the writes of an account session row by its row version, which every update changes.
    private sealed class WriteCounter(PostgresIntegrationTestContext context, Guid sessionId)
    {
        private string? version;

        public int Count { get; private set; }

        // Changes the row as a test step, which is not counted.
        public async Task SetAsync(string assignments)
        {
            await context.ExecuteAsync($"update account_sessions set {assignments} where id = '{sessionId}';");
            version = await ReadVersionAsync();
        }

        public async Task CountAsync()
        {
            var current = await ReadVersionAsync();
            if (!string.Equals(current, version, StringComparison.Ordinal))
            {
                Count++;
                version = current;
            }
        }

        private Task<string?> ReadVersionAsync()
        {
            return context.ExecuteScalarStringAsync(
                $"select xmin::text from account_sessions where id = '{sessionId}';");
        }
    }
}
