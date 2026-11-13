using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Testing;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.Testing;
using JoinShardRequest = Mmorpg.GameService.Placement.JoinShardRequest;

namespace Mmorpg.Integration.Tests;

// After a leave the worker keeps the character's body in the world and its simulation session
// active, without a release. Against GameService's routes, the account then cannot enter another
// character, while entering the same character is a reconnect of the same session, which the
// worker's session store and entity registry give the body's entity. A logout and a new login in
// between change neither.
public sealed class LingeringBodyFlowIntegrationTests
{
    private const string ShardId = "local-shard-1";
    private const string WorkerId = "local-simulation-worker-1";
    private const string WorkerRuntimeId = "integration-worker-runtime";

    [PostgresIntegrationFact]
    public async Task WhileTheBodyStaysTheAccountEntersOnlyTheSameCharacterAgainAsTheSameEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("lingering@example.com", "lingering_player", "Lingering Hero");
        var other = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Waiting Hero"),
            cancellationToken);
        Assert.True(other.Succeeded, other.Error?.Message);
        using var httpClient = new HttpClient(new InProcessWorkerRoutes(context, WorkerId))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);
        var sessions = new ActiveSimulationSessionStore();
        var entities = new SimulationEntityRegistry();

        var first = await JoinAsync(context, client, player.Account, player.Character.Id, cancellationToken);
        Assert.False(first.IsReconnect);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, sessions.Register(first));
        var body = entities.RegisterPlayer(first, () => Movement(first)).Entity;

        // The player left: the worker kept the body and its session, so it released nothing.
        var blocked = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(other.Value!.Id),
            cancellationToken);
        Assert.False(blocked.Succeeded);
        Assert.Equal("account_character_already_active", blocked.Error!.Code);

        var second = await JoinAsync(context, client, player.Account, player.Character.Id, cancellationToken);
        Assert.True(second.IsReconnect);
        Assert.Equal(first.SimulationSessionId, second.SimulationSessionId);
        Assert.NotEqual(first.SimulationSessionToken, second.SimulationSessionToken);
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, sessions.Register(second));
        Assert.True(sessions.IsSupersededByReconnect(first));
        sessions.TryGet(second.CharacterId, out var registered);
        var resumed = entities.RegisterPlayer(registered!, () => throw new InvalidOperationException("A reconnect keeps its entity."));
        Assert.False(resumed.IsNewEntity);
        Assert.Null(resumed.ReplacedEntity);
        Assert.Same(body, resumed.Entity);
        Assert.Equal(second.SimulationSessionToken, resumed.Entity.Session.SimulationSessionToken);
        Assert.Equal(1, entities.PlayerCount);
    }

    // The player leaves and logs out: the body stays, GameService's heartbeat tells the worker that
    // the account session ended without ending the session, and after a new login the same
    // character is a reconnect of the body's session, which the worker gives the body's entity.
    [PostgresIntegrationFact]
    public async Task AfterALeaveAndALogoutTheNextLoginEntersTheSameCharacterAsTheSameEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("relogin@example.com", "relogin_player", "Relogin Hero");
        using var httpClient = new HttpClient(new InProcessWorkerRoutes(context, WorkerId))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);
        var sessions = new ActiveSimulationSessionStore();
        var entities = new SimulationEntityRegistry();
        var first = await JoinAsync(context, client, player.Account, player.Character.Id, cancellationToken);
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, sessions.Register(first));
        var body = entities.RegisterPlayer(first, () => Movement(first)).Entity;

        await LogOutAsync(context, player, cancellationToken);
        var heartbeat = await client.HeartbeatSimulationSessionAsync(
            first.SimulationSessionId,
            WorkerRuntimeId,
            first.SimulationSessionToken,
            cancellationToken);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Code + ": " + heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.Equal(GameServiceOutcome.AccountSessionEndedCode, heartbeat.Value.AccountSessionEnded);
        Assert.True(sessions.MarkAccountSessionEnded(
            first.CharacterId,
            first.SimulationSessionId,
            first.SimulationSessionToken,
            heartbeat.Value.AccountSessionEnded!));
        Assert.True(sessions.IsCurrent(first, DateTime.UtcNow));

        var account = await LogInAsync(context, "relogin_player", cancellationToken);
        var second = await JoinAsync(context, client, account, player.Character.Id, cancellationToken);

        Assert.True(second.IsReconnect);
        Assert.Equal(first.SimulationSessionId, second.SimulationSessionId);
        Assert.Equal(ActiveSimulationSessionRegistration.Reconnected, sessions.Register(second));
        sessions.TryGet(second.CharacterId, out var registered);
        Assert.False(sessions.TryGetAccountSessionEnd(registered!, out _));
        var resumed = entities.RegisterPlayer(registered!, () => throw new InvalidOperationException("A reconnect keeps its entity."));
        Assert.False(resumed.IsNewEntity);
        Assert.Same(body, resumed.Entity);
        var renewed = await client.HeartbeatSimulationSessionAsync(
            second.SimulationSessionId,
            WorkerRuntimeId,
            second.SimulationSessionToken,
            cancellationToken);
        Assert.True(renewed.Succeeded, renewed.Error?.Code + ": " + renewed.Error?.Message);
        Assert.Null(renewed.Value!.AccountSessionEnded);
    }

    // The same flow, but after the new login the account asks for another character, which stays
    // refused while the body of the first one stands.
    [PostgresIntegrationFact]
    public async Task AfterALeaveAndALogoutTheNextLoginCannotEnterAnotherCharacterWhileTheBodyStands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("relogin-other@example.com", "relogin_other", "Relogin Other");
        var other = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new CreateCharacterRequest("Benched Hero"),
            cancellationToken);
        Assert.True(other.Succeeded, other.Error?.Message);
        using var httpClient = new HttpClient(new InProcessWorkerRoutes(context, WorkerId))
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        var client = new GameServiceClient(httpClient);
        await JoinAsync(context, client, player.Account, player.Character.Id, cancellationToken);

        await LogOutAsync(context, player, cancellationToken);
        var account = await LogInAsync(context, "relogin_other", cancellationToken);
        var blocked = await context.ShardService.CreateJoinTicketAsync(
            account,
            ShardId,
            new JoinShardRequest(other.Value!.Id),
            cancellationToken);

        Assert.False(blocked.Succeeded);
        Assert.Equal("account_character_already_active", blocked.Error!.Code);
    }

    private static async Task LogOutAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        CancellationToken cancellationToken)
    {
        var logout = await context.SessionService.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            Mmorpg.GameService.Accounts.Contracts.AccountSessionRevocationReason.Logout,
            cancellationToken);
        Assert.True(logout.Succeeded, logout.Error?.Message);
    }

    private static async Task<Mmorpg.GameService.Accounts.Contracts.AuthenticatedAccount> LogInAsync(
        PostgresIntegrationTestContext context,
        string username,
        CancellationToken cancellationToken)
    {
        var login = await context.AccountService.LoginAsync(
            new Mmorpg.GameService.Accounts.LoginAccountRequest(username, "TestPass123!"),
            cancellationToken);
        Assert.True(login.Succeeded, login.Error?.Message);
        var account = await context.SessionService.AuthenticateTokenAsync(login.Value!.SessionToken, cancellationToken);
        Assert.NotNull(account);
        return account;
    }

    // Asks GameService for a ticket and consumes it as the worker's join service does.
    private static async Task<ActiveSimulationSession> JoinAsync(
        PostgresIntegrationTestContext context,
        GameServiceClient client,
        Mmorpg.GameService.Accounts.Contracts.AuthenticatedAccount account,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            account,
            ShardId,
            new JoinShardRequest(characterId),
            cancellationToken);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await client.ConsumeJoinTicketAsync(
            join.Value!.JoinTicket,
            WorkerId,
            WorkerRuntimeId,
            ShardId,
            cancellationToken);
        Assert.True(consumed.Succeeded, consumed.Error?.Code + ": " + consumed.Error?.Message);
        Assert.Equal(join.Value.IsReconnect, consumed.Value!.IsReconnect);
        return ActiveSimulationSession.FromJoinTicket(consumed.Value, DateTime.UtcNow);
    }

    private static AuthoritativePlayerMovement Movement(ActiveSimulationSession session)
    {
        var settings = new MovementSimulationSettings(30, 5f, 720f, -24f, -2f, 0f, -14f, 14f, -14f, 14f);
        var collisionWorld = new DynamicCollisionWorld(32f);
        return new AuthoritativePlayerMovement(
            PlayerMovementSimulation.CreateInitialState(settings, collisionWorld, 0f, 0f, -1f, 0f),
            settings,
            session.CarryState,
            collisionWorld);
    }
}
