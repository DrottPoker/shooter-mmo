using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Accounts.Tests;

// Account sessions count from the expiry of their token, also when they were revoked earlier, so a
// replaced token answers account_session_replaced for its whole lifetime and the retention window
// after it. Mmorpg.Integration.Tests checks that a session that Placement's rows name stays.
public sealed class AccountSessionRetentionIntegrationTests
{
    private const string Password = "TestPass123!";
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [PostgresIntegrationFact]
    public async Task AReplacedSessionKeepsItsAnswerUntilItsTokenExpiredAndTheWindowPassed()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var replacedToken = player.Registration.SessionToken;
        var login = await context.AccountService.LoginAsync(
            new LoginAccountRequest("integration_player", Password),
            CancellationToken.None);
        Assert.True(login.Succeeded, login.Error?.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "update account_sessions set revoked_at = now() - interval '20 days' where id = @SessionId;",
            new { player.Registration.SessionId });

        // Revoked long ago, but its token is still within its lifetime, so it is not due.
        var revoked = await RetentionPasses.RunAsync(context, Day, Day);
        Assert.Equal(0, revoked.Task("account_sessions").Deleted);
        Assert.Equal(
            AccountSessionErrorCode.SessionReplaced,
            (await context.SessionService.ResolveTokenAsync(replacedToken, CancellationToken.None)).ErrorCode);

        await connection.ExecuteAsync(
            """
            update account_sessions
            set created_at = now() - interval '21 days',
                expires_at = now() - interval '2 days'
            where id = @SessionId;
            """,
            new { player.Registration.SessionId });
        var expired = await RetentionPasses.RunAsync(context, Day, Day);

        Assert.Equal(1, expired.Task("account_sessions").Deleted);
        Assert.Equal(
            new[] { login.Value!.SessionId },
            (await connection.QueryAsync<Guid>(
                "select id from account_sessions where account_id = @AccountId;",
                new { player.Registration.AccountId })).ToArray());
        Assert.Equal(
            "invalid_session_token",
            (await context.SessionService.ResolveTokenAsync(replacedToken, CancellationToken.None)).ErrorCode);
    }

    [PostgresIntegrationFact]
    public async Task AnActiveSessionIsNeverDue()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var pass = await RetentionPasses.RunAsync(context, Day, TimeSpan.Zero);

        Assert.Equal(0, pass.Task("account_sessions").Deleted);
        Assert.NotNull(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None));
    }
}
