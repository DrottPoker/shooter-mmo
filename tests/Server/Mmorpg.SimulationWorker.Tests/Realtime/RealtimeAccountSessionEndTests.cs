using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// When the account session of a simulation session ends, by a logout, a new login, or its expiry,
// the session goes on: GameService says so in the session heartbeat and refuses what a client asks
// for. The worker then ends the connection with the disconnect the client got before, and the
// character stays in the world as a lingering body for its full time, which a reconnect of the
// new account session resumes. Nothing removes the entity or invalidates the session.
public sealed class RealtimeAccountSessionEndTests
{
    // A heartbeat that names the end throws the connected client out with its code, and the body
    // stays its full time and then goes with its final state and the release of its session.
    [Theory]
    [InlineData("account_session_replaced", "account_session_replaced")]
    [InlineData("account_session_ended", "session_revoked")]
    public async Task AHeartbeatThatSaysTheAccountSessionEndedEndsTheConnectionAndTheBodyStaysItsFullTime(
        string accountSessionEnd,
        string disconnectCode)
    {
        await using var harness = Create(bodySeconds: 2);
        var observerCharacter = harness.AddCharacter("End Watcher");
        var character = harness.AddCharacter("Logged Out Hero") with
        {
            SavedState = Saved(harness, 1.5f, 2f, yaw: 30f, health: 61, mana: 17)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => observer.Spawns.Any(spawn => spawn.EntityId == entityId),
            "the observer to see the hero");
        harness.GameService.OnSessionHeartbeat = (sessionId, _, _) => Task.FromResult(
            harness.GameService.DefaultSessionHeartbeat(
                sessionId,
                sessionId == character.SimulationSessionId ? accountSessionEnd : null));

        using var heartbeats = Heartbeats(harness);
        await heartbeats.StartAsync(TestContext.Current.CancellationToken);
        await harness.PollUntilAsync(() => client.ServerDisconnect is not null, "the disconnect");
        var endedAt = DateTime.UtcNow;

        Assert.Equal(disconnectCode, client.ServerDisconnect!.Code);
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        Assert.True(harness.SessionStore.TryGet(character.CharacterId, out var current));
        Assert.True(harness.SessionStore.IsCurrent(current!, DateTime.UtcNow));
        Assert.False(harness.SessionStore.TryTakeInvalidation(character.SimulationSessionId, out _));
        Assert.DoesNotContain(observer.Despawns, despawn => despawn.EntityId == entityId);
        Assert.Empty(harness.GameService.Releases);

        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == entityId),
            "the body to go");
        var despawn = Despawn(observer, entityId);
        Assert.Equal(LingeringBodyTimer.TimeUpReason, despawn.Value.Reason);
        Assert.True(
            despawn.Packet.ReceivedAtUtc - endedAt >= TimeSpan.FromSeconds(1.5),
            "The body must stay its full time.");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        var release = Assert.Single(harness.GameService.Releases);
        Assert.Equal(character.SimulationSessionId, release.SimulationSessionId);
        AssertState(release.Request.FinalState, 1.5f, 2f, 30f, 61, 17);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
        Assert.Null(observer.ServerDisconnect);
    }

    // A player who left already stands as a body, so the end of its account session changes
    // nothing: it stays its time and goes with the release of its session.
    [Fact]
    public async Task ABodyWhoseAccountSessionEndsStaysAsItIs()
    {
        await using var harness = Create(bodySeconds: 2);
        var observerCharacter = harness.AddCharacter("Calm Watcher");
        var character = harness.AddCharacter("Departed Hero");
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        client.SendLeave(character.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
        var leftAt = DateTime.UtcNow;

        Assert.True(harness.SessionStore.MarkAccountSessionEnded(
            character.CharacterId,
            character.SimulationSessionId,
            character.SimulationSessionToken,
            "account_session_ended"));

        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == entityId),
            "the body to go");
        var despawn = Despawn(observer, entityId);
        Assert.Equal(LingeringBodyTimer.TimeUpReason, despawn.Value.Reason);
        Assert.True(despawn.Packet.ReceivedAtUtc - leftAt >= TimeSpan.FromSeconds(1.8));
        Assert.Null(client.ServerDisconnect);
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        Assert.Equal(character.SimulationSessionId, Assert.Single(harness.GameService.Releases).SimulationSessionId);
    }

    // The player logs in again and enters the same character while its body stands: the reconnect
    // resumes the body's entity, and the new generation carries no end.
    [Fact]
    public async Task AReconnectAfterTheAccountSessionEndedResumesTheBody()
    {
        await using var harness = Create(bodySeconds: 2);
        var observerCharacter = harness.AddCharacter("Returning Watcher");
        var character = harness.AddCharacter("Relogged Hero");
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var first = await harness.JoinAsync(character);
        var entityId = first.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => observer.Spawns.Any(spawn => spawn.EntityId == entityId),
            "the observer to see the hero");

        Assert.True(harness.SessionStore.MarkAccountSessionEnded(
            character.CharacterId,
            character.SimulationSessionId,
            character.SimulationSessionToken,
            "account_session_replaced"));
        await harness.PollUntilAsync(() => first.ServerDisconnect is not null, "the disconnect");
        Assert.Equal("account_session_replaced", first.ServerDisconnect!.Code);
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");

        var reconnect = character with { Ticket = "ticket-relogged-hero", SimulationSessionToken = "relogged-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "Relogged Hero again");

        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.Equal(entityId, second.JoinAccepted.ControlledEntityId);
        Assert.True(harness.SessionStore.TryGet(character.CharacterId, out var current));
        Assert.Equal("relogged-token", current!.SimulationSessionToken);
        Assert.False(harness.SessionStore.TryGetAccountSessionEnd(current, out _));

        // Past the body's time nothing went and nothing was released.
        await harness.PollForAsync(TimeSpan.FromSeconds(3));
        Assert.Null(second.ServerDisconnect);
        Assert.Empty(harness.GameService.Releases);
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        Assert.DoesNotContain(observer.Despawns, despawn => despawn.EntityId == entityId);
    }

    // GameService refuses an item operation of a client whose account session ended: the client
    // gets the refusal and the disconnect, and the body stays its time and is released.
    [Fact]
    public async Task AnItemOperationRefusedForAnEndedAccountSessionEndsOnlyTheConnection()
    {
        await using var harness = Create(bodySeconds: 2);
        var observerCharacter = harness.AddCharacter("Refusal Watcher");
        var character = harness.AddCharacter("Refused Hero");
        harness.GameService.OnItemOperation = (_, _, _) => Task.FromResult(ScriptedGameServiceHandler.Problem(
            HttpStatusCode.Unauthorized,
            "account_session_ended",
            "The account session of this simulation session ended."));
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;

        client.SendItemIntent(RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 1));
        await harness.PollUntilAsync(() => client.ServerDisconnect is not null, "the disconnect");

        var result = Assert.Single(client.Decode<RealtimeItemOperationResult>(
            RealtimeMessageType.ItemOperationResult,
            RealtimeProtocol.TryDecodeItemOperationResult));
        Assert.False(result.Succeeded);
        Assert.Equal("account_session_ended", result.Error!.Code);
        Assert.Equal("session_revoked", client.ServerDisconnect!.Code);
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        Assert.False(harness.SessionStore.TryTakeInvalidation(character.SimulationSessionId, out _));

        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == entityId),
            "the body to go");
        Assert.Equal(LingeringBodyTimer.TimeUpReason, Despawn(observer, entityId).Value.Reason);
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
    }

    // An answer that says the account session ended reaches a connected peer: it gets the
    // operation's result, then the disconnect, and its entity becomes a body of the same session.
    [Theory]
    [InlineData("account_session_replaced", "account_session_replaced")]
    [InlineData("account_session_ended", "session_revoked")]
    public void AnAnswerThatTheAccountSessionEndedLeavesTheConnectedPlayersBody(string code, string disconnectCode)
    {
        using var fixture = new RealtimeLoopFixture();
        using var gameService = new FixtureGameService();
        var handler = fixture.CreateItemHandler(gameService.Operations(fixture));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;

        handler.Complete(new ItemOperationCompleted(player.Context, player.Session, Refused(code)));

        Assert.Equal(
            [RealtimeMessageType.ItemOperationResult, RealtimeMessageType.ServerDisconnect],
            player.Peer.Sent.Select(sent => sent.Type));
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(player.Peer.Sent[^1].Data, out var disconnect, out var error), error);
        Assert.Equal(disconnectCode, disconnect.Code);
        Assert.Null(player.Context.Session);
        Assert.NotNull(player.Context.DisconnectAfterUtc);
        Assert.True(fixture.Bodies.IsBody(player.Entity.NetworkEntityId));
        Assert.Equal(1, fixture.Entities.PlayerCount);
        Assert.True(fixture.SessionStore.IsCurrent(player.Session, DateTime.UtcNow));
        Assert.False(fixture.SessionStore.TryTakeInvalidation(player.Session.SimulationSessionId, out _));
        Assert.True(fixture.SessionStore.TryGetAccountSessionEnd(player.Session, out var recorded));
        Assert.Equal(code, recorded);
    }

    // The same answer arriving after the connection already ended finds a body: nothing more
    // happens to it, its session, or anyone's connection.
    [Fact]
    public void AnAnswerThatTheAccountSessionEndedChangesNothingForABody()
    {
        using var fixture = new RealtimeLoopFixture();
        using var gameService = new FixtureGameService();
        var handler = fixture.CreateItemHandler(gameService.Operations(fixture));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        Assert.True(fixture.Teardown.LeaveBody(player.Context, player.Session));

        handler.Complete(new ItemOperationCompleted(player.Context, player.Session, Refused("account_session_ended")));

        Assert.Empty(player.Peer.Sent);
        Assert.True(fixture.Bodies.IsBody(player.Entity.NetworkEntityId));
        Assert.Equal(1, fixture.Entities.PlayerCount);
        Assert.True(fixture.SessionStore.IsCurrent(player.Session, DateTime.UtcNow));
        Assert.False(fixture.SessionStore.TryTakeInvalidation(player.Session.SimulationSessionId, out _));
        Assert.False(player.Context.AuthorityOperationInFlight);
    }

    // The refusal that GameService sends for an ended account session, as the item interaction
    // service turns it into a result.
    private static SimulationItemInteractionResult Refused(string code)
    {
        Assert.True(GameServiceOutcome.EndsAccountSession(code));
        return SimulationItemInteractionResult.Rejected(
            RealtimeProtocolRejectionTests.CreateItemIntent(),
            code,
            "The account session of this simulation session ended.",
            shouldDisconnect: true);
    }

    private static RealtimeServerHarness Create(int bodySeconds)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            // Without regeneration the vitals stay as they were.
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll(),
            LingeringBodySeconds = bodySeconds
        });
    }

    // The worker's session heartbeat over the harness's GameService, every 100 milliseconds.
    // Disposing it stops the loop without the releases that a stop sends.
    private static SimulationSessionHeartbeatService Heartbeats(RealtimeServerHarness harness)
    {
        return new SimulationSessionHeartbeatService(
            harness.GameServiceClient,
            harness.ReleaseService,
            harness.SessionStore,
            harness.CharacterItemStateStore,
            harness.Config with { SimulationSessionHeartbeatInterval = TimeSpan.FromMilliseconds(100) },
            NullLogger<SimulationSessionHeartbeatService>.Instance);
    }

    private static (RecordedRealtimePacket Packet, RealtimeEntityDespawn Value) Despawn(RealtimeRecordingClient client, ulong entityId)
    {
        return client.DecodeWithPackets<RealtimeEntityDespawn>(
                RealtimeMessageType.EntityDespawn,
                RealtimeProtocol.TryDecodeEntityDespawn)
            .Single(value => value.Value.EntityId == entityId);
    }

    private static SimulationSavedCharacterState Saved(
        RealtimeServerHarness harness,
        float x,
        float z,
        float yaw,
        int health,
        int mana)
    {
        return new SimulationSavedCharacterState(
            RealtimeServerHarness.WorldId,
            harness.StaticCollisionWorld.Revision,
            x,
            0f,
            z,
            yaw,
            health,
            mana);
    }

    private static void AssertState(
        SimulationCharacterStateRequest? state,
        float x,
        float z,
        float yaw,
        int health,
        int mana)
    {
        Assert.NotNull(state);
        Assert.Equal((x, 0f, z, yaw, health, mana), (state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees, state.Health, state.Mana));
    }

    // The item operations of a loop fixture, over a scripted GameService that no test here calls.
    private sealed class FixtureGameService : IDisposable
    {
        private readonly HttpClient httpClient = new(new ScriptedGameServiceHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };

        public ItemAuthorityOperations Operations(RealtimeLoopFixture fixture)
        {
            return new ItemAuthorityOperations(
                new SimulationItemInteractionService(new GameServiceClient(httpClient)),
                fixture.Completions,
                NullLogger.Instance);
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }
}
