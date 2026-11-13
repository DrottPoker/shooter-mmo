using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Placement.Tests;

// When an account session ends, by a logout, a new login, or its expiry, the account's simulation
// session is marked instead of released, so the character's body stays in the world: its lease goes
// on, the heartbeat names the end, and only a reconnect of the same character by the new account
// session clears the mark. Nothing that holds only the old account session gets a ticket, another
// character stays refused, and the session still ends by its lease or a worker release.
public sealed class AccountSessionEndIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private const string Password = "TestPass123!";

    [PostgresIntegrationFact]
    public async Task ALogoutMarksTheSimulationSessionWithoutReleasingIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        var pending = await TicketAsync(context, player.Account, player.Character.Id);
        Assert.True(pending.Succeeded, pending.Error?.Message);
        Assert.True(pending.Value!.IsReconnect);

        await LogOutAsync(context, player);

        Assert.Equal("live marked", await StateAsync(context, session.SimulationSessionId));
        var heartbeat = await HeartbeatAsync(context, session);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.Equal(SimulationAccountSessionEndCodes.Ended, heartbeat.Value.AccountSessionEnded);
        Assert.True(heartbeat.Value.LeaseDurationMilliseconds > 0);
        Assert.Equal("live marked", await StateAsync(context, session.SimulationSessionId));

        // The pending ticket of the old account session was consumed with the logout.
        var consumedPending = await ConsumeAsync(context, pending.Value.JoinTicket);
        Assert.Equal(StatusCodes.Status401Unauthorized, consumedPending.StatusCode);
        Assert.Equal("invalid_ticket", consumedPending.Error!.Code);
    }

    [PostgresIntegrationFact]
    public async Task ANewLoginMarksTheSimulationSessionAsReplacedWithoutReleasingIt()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player.Account, player.Character.Id);

        await LogInAsync(context, player);

        Assert.Equal("live marked", await StateAsync(context, session.SimulationSessionId));
        var heartbeat = await HeartbeatAsync(context, session);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.Equal(SimulationAccountSessionEndCodes.Replaced, heartbeat.Value.AccountSessionEnded);
    }

    // The new account session enters the same character: the existing reconnect of the same
    // session on the same worker, which binds the session to the new account session and clears
    // the mark, and the generation of the old token is refused.
    [PostgresIntegrationFact]
    public async Task TheSameCharacterOfTheNewAccountSessionIsAReconnectThatBindsItAndClearsTheMark()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var first = await JoinAsync(context, player.Account, player.Character.Id);
        var account = await LogInAsync(context, player);

        var second = await JoinAsync(context, account, player.Character.Id);

        Assert.True(second.IsReconnect);
        Assert.Equal(first.SimulationSessionId, second.SimulationSessionId);
        Assert.Equal("live", await StateAsync(context, second.SimulationSessionId));
        Assert.Equal(
            account.SessionId.ToString(),
            await context.ExecuteScalarStringAsync(
                $"select account_session_id::text from character_simulation_sessions where id = '{second.SimulationSessionId}';"));
        var heartbeat = await HeartbeatAsync(context, second);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Null(heartbeat.Value!.AccountSessionEnded);
        var oldGeneration = await HeartbeatAsync(context, first);
        Assert.Equal(StatusCodes.Status401Unauthorized, oldGeneration.StatusCode);
    }

    [PostgresIntegrationFact]
    public async Task AnotherCharacterIsRefusedWhileTheMarkedSessionLives()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var other = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new Mmorpg.GameService.Characters.CreateCharacterRequest("Waiting Hero"),
            CancellationToken.None);
        Assert.True(other.Succeeded, other.Error?.Message);
        await JoinAsync(context, player.Account, player.Character.Id);
        await LogOutAsync(context, player);
        var account = await LogInAsync(context, player);

        var refused = await TicketAsync(context, account, other.Value!.Id);

        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        Assert.Equal("account_character_already_active", refused.Error!.Code);
    }

    // Whoever holds only the old account session gets no ticket: its token no longer
    // authenticates, and a ticket written for it, as a caller past the authentication would get,
    // is refused when the worker consumes it.
    [PostgresIntegrationFact]
    public async Task TheOldAccountSessionGetsNoUsableTicket()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        await JoinAsync(context, player.Account, player.Character.Id);
        await LogInAsync(context, player);

        Assert.Null(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None));
        var stale = await TicketAsync(context, player.Account, player.Character.Id);
        Assert.True(stale.Succeeded, stale.Error?.Message);
        var consumed = await ConsumeAsync(context, stale.Value!.JoinTicket);

        Assert.Equal(StatusCodes.Status401Unauthorized, consumed.StatusCode);
        Assert.Equal("invalid_ticket", consumed.Error!.Code);
    }

    // An account session that expired ends like a logout: every check counts it at once, and the
    // heartbeat marks the session and names the end.
    [PostgresIntegrationFact]
    public async Task AnExpiredAccountSessionIsMarkedByTheHeartbeat()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        await context.ExecuteAsync(
            $"update account_sessions set expires_at = now() - interval '1 second' where id = '{player.Account.SessionId}';");

        Assert.Equal("live", await StateAsync(context, session.SimulationSessionId));
        var facts = await LockAsync(context, session.SimulationSessionId);
        Assert.True(facts.IsLive);
        Assert.Equal(SimulationAccountSessionEnd.Ended, facts.AccountSessionEnd);
        Assert.False(facts.IsActive);

        var heartbeat = await HeartbeatAsync(context, session);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal(SimulationAccountSessionEndCodes.Ended, heartbeat.Value!.AccountSessionEnded);
        Assert.Equal("live marked", await StateAsync(context, session.SimulationSessionId));
    }

    // Nobody renews a marked session whose worker died, so its lease expires as any lease does,
    // and the account can enter another character.
    [PostgresIntegrationFact]
    public async Task AMarkedSessionStillExpiresByItsLease()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var other = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new Mmorpg.GameService.Characters.CreateCharacterRequest("Patient Hero"),
            CancellationToken.None);
        Assert.True(other.Succeeded, other.Error?.Message);
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        await LogOutAsync(context, player);
        var account = await LogInAsync(context, player);
        await context.ExecuteAsync(
            $"update character_simulation_sessions set expires_at = created_at + interval '1 millisecond' where id = '{session.SimulationSessionId}';");

        var heartbeat = await HeartbeatAsync(context, session);
        Assert.Equal(StatusCodes.Status401Unauthorized, heartbeat.StatusCode);
        Assert.Equal("invalid_simulation_session", heartbeat.Error!.Code);
        var entered = await JoinAsync(context, account, other.Value!.Id);

        Assert.False(entered.IsReconnect);
        Assert.Equal("released marked", await StateAsync(context, session.SimulationSessionId));
    }

    // The worker releases a marked session when the body goes, with its final state, as it releases
    // any session.
    [PostgresIntegrationFact]
    public async Task AWorkerReleaseEndsAMarkedSessionWithItsFinalState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player.Account, player.Character.Id);
        await LogOutAsync(context, player);

        var released = await context.SimulationSessionService.ReleaseAsync(
            session.SimulationSessionId,
            WorkerId,
            new SimulationSessionReleaseRequest(
                RuntimeId,
                session.SimulationSessionToken,
                new SimulationCharacterStateRequest(1, 2f, 0f, -2f, 90f, 33, 4)),
            CancellationToken.None);

        Assert.True(released.Succeeded, released.Error?.Message);
        Assert.True(released.Value!.Released);
        Assert.True(released.Value.FinalStateSaved);
        Assert.Equal("released marked", await StateAsync(context, session.SimulationSessionId));
        var account = await LogInAsync(context, player);
        var next = await JoinAsync(context, account, player.Character.Id);
        Assert.False(next.IsReconnect);
        Assert.Equal(33, next.SavedState!.Health);
    }

    // The session authority names the end for every module, and a change of the look, which a
    // client asks for, is refused with the code of the end.
    [PostgresIntegrationFact]
    public async Task TheSessionAuthorityNamesTheEndAndALookChangeIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var replacedPlayer = await context.RegisterPlayerAsync("replaced@example.com", "replaced_player", "Replaced Hero");
        var loggedOutPlayer = await context.RegisterPlayerAsync("logged-out@example.com", "logged_out_player", "Logged Out Hero");
        var replaced = await JoinAsync(context, replacedPlayer.Account, replacedPlayer.Character.Id);
        var loggedOut = await JoinAsync(context, loggedOutPlayer.Account, loggedOutPlayer.Character.Id);
        var before = await LockAsync(context, replaced.SimulationSessionId);
        Assert.True(before.IsActive);
        Assert.Null(before.AccountSessionEnd);

        await LogInAsync(context, replacedPlayer, "replaced_player");
        await LogOutAsync(context, loggedOutPlayer);

        var replacedFacts = await LockAsync(context, replaced.SimulationSessionId);
        var loggedOutFacts = await ReadAsync(context, loggedOut.SimulationSessionId);
        Assert.Equal((true, (SimulationAccountSessionEnd?)SimulationAccountSessionEnd.Replaced, false), (replacedFacts.IsLive, replacedFacts.AccountSessionEnd, replacedFacts.IsActive));
        Assert.Equal((true, (SimulationAccountSessionEnd?)SimulationAccountSessionEnd.Ended, false), (loggedOutFacts.SessionIsLive, loggedOutFacts.AccountSessionEnd, loggedOutFacts.SessionIsActive));
        var appearance = context.Services.GetRequiredService<SimulationAppearanceService>();
        var replacedLook = await appearance.ChangeAsync(
            replaced.SimulationSessionId,
            WorkerId,
            new SimulationAppearanceChangeRequest(RuntimeId, replaced.SimulationSessionToken, SimulationAppearanceService.HeadwearChange, true, null, null),
            CancellationToken.None);
        var loggedOutLook = await appearance.ChangeAsync(
            loggedOut.SimulationSessionId,
            WorkerId,
            new SimulationAppearanceChangeRequest(RuntimeId, loggedOut.SimulationSessionToken, SimulationAppearanceService.HeadwearChange, true, null, null),
            CancellationToken.None);

        Assert.Equal(
            (StatusCodes.Status401Unauthorized, SimulationAccountSessionEndCodes.Replaced),
            (replacedLook.StatusCode, replacedLook.Error!.Code));
        Assert.Equal(
            (StatusCodes.Status401Unauthorized, SimulationAccountSessionEndCodes.Ended),
            (loggedOutLook.StatusCode, loggedOutLook.Error!.Code));
    }

    private static Task<ServiceResult<JoinShardResponse>> TicketAsync(
        PostgresIntegrationTestContext context,
        AuthenticatedAccount account,
        Guid characterId)
    {
        return context.ShardService.CreateJoinTicketAsync(
            account,
            ShardId,
            new JoinShardRequest(characterId),
            CancellationToken.None);
    }

    private static Task<ServiceResult<ConsumedSimulationJoinTicketResponse>> ConsumeAsync(
        PostgresIntegrationTestContext context,
        string ticket)
    {
        return context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(ticket, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        AuthenticatedAccount account,
        Guid characterId)
    {
        var join = await TicketAsync(context, account, characterId);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await ConsumeAsync(context, join.Value!.JoinTicket);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(join.Value.IsReconnect, consumed.Value!.IsReconnect);
        return consumed.Value;
    }

    private static async Task LogOutAsync(PostgresIntegrationTestContext context, IntegrationPlayer player)
    {
        var logout = await context.SessionService.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);
        Assert.True(logout.Succeeded, logout.Error?.Message);
    }

    // A login of the same account, which replaces its account session.
    private static async Task<AuthenticatedAccount> LogInAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string username = "integration_player")
    {
        var login = await context.AccountService.LoginAsync(
            new LoginAccountRequest(username, Password),
            CancellationToken.None);
        Assert.True(login.Succeeded, login.Error?.Message);
        Assert.Equal(player.Registration.AccountId, login.Value!.AccountId);
        var account = await context.SessionService.AuthenticateTokenAsync(
            login.Value.SessionToken,
            CancellationToken.None);
        Assert.NotNull(account);
        return account;
    }

    private static Task<ServiceResult<SimulationSessionLeaseResponse>> HeartbeatAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session)
    {
        return context.SimulationSessionService.HeartbeatAsync(
            session.SimulationSessionId,
            WorkerId,
            new SimulationSessionCredentialRequest(RuntimeId, session.SimulationSessionToken),
            CancellationToken.None);
    }

    private static async Task<LiveSimulationSession> LockAsync(PostgresIntegrationTestContext context, Guid simulationSessionId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var session = await context.Services.GetRequiredService<ISimulationSessionAuthority>().LockForItemMutationAsync(
            new PostgresTransactionScope(connection, transaction),
            simulationSessionId,
            CancellationToken.None);
        await transaction.RollbackAsync();
        return Assert.IsType<LiveSimulationSession>(session);
    }

    private static async Task<SimulationSessionReadAuthority> ReadAsync(PostgresIntegrationTestContext context, Guid simulationSessionId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var session = await context.Services.GetRequiredService<ISimulationSessionAuthority>().GetReadAuthorityAsync(
            new PostgresTransactionScope(connection, transaction),
            simulationSessionId,
            CancellationToken.None);
        await transaction.RollbackAsync();
        return Assert.IsType<SimulationSessionReadAuthority>(session);
    }

    // Whether the session is live or released, and whether it carries the mark.
    private static async Task<string?> StateAsync(PostgresIntegrationTestContext context, Guid simulationSessionId)
    {
        return await context.ExecuteScalarStringAsync(
            $"""
            select case when released_at is null then 'live' else 'released' end
                   || case when account_session_ended_at is null then '' else ' marked' end
            from character_simulation_sessions
            where id = '{simulationSessionId}';
            """);
    }
}
