using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A player comes back while its lingering body dies or its time ends. GameService rotates the
// session's token when it consumes the reconnect's ticket, before the worker learns the new token
// from the join, so the body's release, its death, and its heartbeat may meet the old token
// refused. Whatever the order, the reconnect ends in one of three outcomes: the join resumes the
// body's entity; the body died and went, and the join gives a new entity at the World spawn with
// full health and mana while the corpse lies where the body fell; or the body's time ended, and
// the join gives a new entity where the body stood, with its vitals. A reconnect from another
// client that takes over a connected player's session meets the same refusals and resumes the
// entity too. The real features run tick by tick, and the test decides when GameService answers
// and when the loop finalizes an answer.
public sealed class LingeringBodyReconnectOrderTests
{
    private const string ReconnectTicket = "ticket-of-the-reconnect";
    private const string ReconnectToken = "token-after-the-reconnect";

    // Bot 303 of the soak of 2026-11-16: a wolf killed its body, the death committed, and the body
    // went with the state its respawn would leave. GameService had consumed the reconnect's ticket
    // in the meantime, so it refused the body's release, and the join reached the worker after
    // that refusal, with the place and health that GameService last saved.
    [Fact]
    public async Task ABodyWhoseDeathCommittedComesBackAtTheSpawnWhenTheReconnectRotatedItsSessionFirst()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(minimumDamage: 500, maximumDamage: 500), sessions: true);
        var killer = fixture.Join(1, "Body Killer", [.. CasterEquipment()]);
        var victim = fixture.Join(2, "Fallen Body");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        var session = victim.Context.Session!;
        var bodyEntityId = victim.EntityId;
        var bodyPlace = victim.Entity.Movement.State;
        var deathGate = new TestGate();
        fixture.GameService.OnPlayerDeath = async (_, request, _) =>
        {
            await deathGate.WaitAsync();
            return ScriptedGameServiceHandler.DefaultPlayerDeath(request);
        };
        fixture.GameService.OnRelease = (_, _, _) => Task.FromResult(RefusedCredentials());

        fixture.Leave(victim);
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), bodyEntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the body");
        fixture.RunTick();
        await WaitUntilAsync(() => deathGate.HasEntered, "the death to reach GameService");

        // The death commits, and the body goes with the state its respawn would leave. GameService
        // consumed the reconnect's ticket after the death, so it refuses the release.
        deathGate.Release();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        fixture.RunTick();
        Assert.False(fixture.Entities.TryGetPlayer(bodyEntityId, out _));
        await fixture.WaitForOperationsAsync();
        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal((0f, 100, 100), (release.Request.FinalState!.PositionX, release.Request.FinalState.Health, release.Request.FinalState.Mana));

        // The join of the reconnect arrives with what GameService last saved: the body's place
        // with the health of its last checkpoint.
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(Reconnect(fixture, session, bodyPlace.PositionX, bodyPlace.PositionZ, health: 34, mana: 20));
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        var accepted = JoinAccepted(peer);
        Assert.True(accepted.IsReconnect);
        Assert.NotEqual(bodyEntityId, accepted.ControlledEntityId);
        var start = accepted.InitialPlayerState;
        Assert.Equal((0f, -1f), (start.PositionX, start.PositionZ));
        Assert.True(fixture.Combat.TryGetPlayerCombatant(accepted.ControlledEntityId, out var combatant));
        Assert.Equal((true, 100, 100), (combatant.IsAlive, combatant.Health, combatant.Mana));
        var death = Assert.Single(fixture.GameService.PlayerDeaths);
        Assert.Equal((bodyPlace.PositionX, bodyPlace.PositionZ), ((float)death.PositionX, (float)death.PositionZ));
        Assert.Single(fixture.Core.CorpseStore.ListActive());
        Assert.Equal(0, fixture.ReleaseService.FinalStates.Count);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The same order for a body whose time ended without a death: the join gives a new entity
    // where the body stood, with the health it had when it went, not with the older health that
    // GameService last saved.
    [Fact]
    public async Task ABodyWhoseTimeEndedComesBackWhereItStoodWhenTheReconnectRotatedItsSessionFirst()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(minimumDamage: 10, maximumDamage: 10), sessions: true);
        var attacker = fixture.Join(1, "Body Biter", [.. CasterEquipment()]);
        var victim = fixture.Join(2, "Bitten Body");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        var session = victim.Context.Session!;
        var bodyEntityId = victim.EntityId;
        var bodyPlace = victim.Entity.Movement.State;
        fixture.GameService.OnRelease = (_, _, _) => Task.FromResult(RefusedCredentials());

        fixture.Leave(victim);
        fixture.SendCombatIntent(attacker, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), bodyEntityId));
        RunUntil(fixture, () => fixture.CombatantOf(victim).Health == 90, "one bolt on the body");
        fixture.SendCombatIntent(attacker, RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        RunUntil(fixture, () => !fixture.Entities.TryGetPlayer(bodyEntityId, out _), "the body's time to end", maximumTicks: 30 * fixture.TickRateHz);
        await fixture.WaitForOperationsAsync();
        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal(90, release.Request.FinalState!.Health);

        fixture.GameService.OnConsume = (_, _) => Task.FromResult(Reconnect(fixture, session, -2f, 1f, health: 34, mana: 20));
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        var accepted = JoinAccepted(peer);
        Assert.NotEqual(bodyEntityId, accepted.ControlledEntityId);
        var start = accepted.InitialPlayerState;
        Assert.Equal((bodyPlace.PositionX, bodyPlace.PositionZ), (start.PositionX, start.PositionZ));
        Assert.True(fixture.Combat.TryGetPlayerCombatant(accepted.ControlledEntityId, out var combatant));
        Assert.Equal((90, 100), (combatant.Health, combatant.Mana));
        Assert.Empty(fixture.GameService.PlayerDeaths);
        Assert.Equal(0, fixture.ReleaseService.FinalStates.Count);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // GameService committed nothing for the token that the reconnect rotated, and refuses the
    // death sent with it; the death commits with the reconnect's token. The join reaches the
    // worker after it processed the refusal, so the refusal must not end the session that the
    // join goes on with: the join resumes the dead body, whose death commits once, and the
    // character respawns at the World spawn.
    [Fact]
    public async Task ADeathRefusedForTheTokenThatTheReconnectRotatedCommitsWithTheNewToken()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(minimumDamage: 500, maximumDamage: 500), sessions: true);
        var killer = fixture.Join(1, "Body Killer", [.. CasterEquipment()]);
        var victim = fixture.Join(2, "Returning Victim");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        var session = victim.Context.Session!;
        var bodyEntityId = victim.EntityId;
        var bodyPlace = victim.Entity.Movement.State;
        fixture.GameService.OnPlayerDeath = (_, request, _) => Task.FromResult(
            request.SessionToken == ReconnectToken
                ? ScriptedGameServiceHandler.DefaultPlayerDeath(request)
                : RefusedItemCredentials());
        var consumeGate = new TestGate();
        fixture.GameService.OnConsume = async (_, _) =>
        {
            await consumeGate.WaitAsync();
            return Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0);
        };
        fixture.Leave(victim);

        // GameService consumes the reconnect's ticket and rotates the token; its answer is still
        // on the way to the worker when the bolt kills the body.
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await WaitUntilAsync(() => consumeGate.HasEntered, "the reconnect's ticket to reach GameService");
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), bodyEntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the body");
        fixture.RunTick();
        await WaitUntilAsync(() => fixture.Core.Completions.Count == 1, "the refusal of the death");
        fixture.ProcessCompletions();
        RunTicks(fixture, fixture.TickRateHz / 10);
        Assert.True(fixture.Entities.TryGetPlayer(bodyEntityId, out _), "The refusal ended the session that the reconnect goes on with.");

        consumeGate.Release();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        var accepted = JoinAccepted(peer);
        Assert.True(accepted.IsReconnect);
        Assert.Equal(bodyEntityId, accepted.ControlledEntityId);
        Assert.Equal((bodyPlace.PositionX, bodyPlace.PositionZ), (accepted.InitialPlayerState.PositionX, accepted.InitialPlayerState.PositionZ));

        await RunUntilAsync(fixture, () => fixture.Combat.TryGetPlayerCombatant(bodyEntityId, out var dead) && dead.IsAlive, "the respawn");
        var respawned = fixture.Entities.TryGetPlayer(bodyEntityId, out var entity) ? entity!.Movement.State : default;
        Assert.Equal((0f, -1f), (respawned.PositionX, respawned.PositionZ));
        Assert.True(fixture.Combat.TryGetPlayerCombatant(bodyEntityId, out var combatant));
        Assert.Equal((100, 100), (combatant.Health, combatant.Mana));
        var deaths = fixture.GameService.PlayerDeaths;
        Assert.Equal(2, deaths.Count);
        Assert.Equal(deaths[0].DeathEventId, deaths[1].DeathEventId);
        Assert.Equal([session.SimulationSessionToken, ReconnectToken], deaths.Select(death => death.SessionToken));
        Assert.Single(fixture.Core.CorpseStore.ListActive());
        Assert.Empty(fixture.GameService.Releases);
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.Equal(ReconnectToken, fixture.SessionStore.ListActiveSessions().Single(value => value.CharacterId == session.CharacterId).SimulationSessionToken);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The join resumed the dead body before GameService's refusal of the old token arrived: the
    // refusal belongs to the generation that the reconnect replaced, so it neither disconnects
    // the returning player nor ends its session, and the death commits with the new token.
    [Fact]
    public async Task ARefusalOfTheReplacedTokenAfterTheJoinResumedTheBodyEndsNothing()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(minimumDamage: 500, maximumDamage: 500), sessions: true);
        var killer = fixture.Join(1, "Body Killer", [.. CasterEquipment()]);
        var victim = fixture.Join(2, "Resumed Victim");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        var session = victim.Context.Session!;
        var bodyEntityId = victim.EntityId;
        var deathGate = new TestGate();
        fixture.GameService.OnPlayerDeath = async (_, request, _) =>
        {
            if (request.SessionToken == ReconnectToken)
            {
                return ScriptedGameServiceHandler.DefaultPlayerDeath(request);
            }

            await deathGate.WaitAsync();
            return RefusedItemCredentials();
        };
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0));
        fixture.Leave(victim);
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), bodyEntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the body");
        fixture.RunTick();
        await WaitUntilAsync(() => deathGate.HasEntered, "the death to reach GameService");

        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await WaitUntilAsync(() => fixture.Core.Completions.Count == 1, "the join");
        fixture.ProcessCompletions();
        Assert.Equal(bodyEntityId, JoinAccepted(peer).ControlledEntityId);

        deathGate.Release();
        await RunUntilAsync(fixture, () => fixture.Combat.TryGetPlayerCombatant(bodyEntityId, out var dead) && dead.IsAlive, "the respawn");
        Assert.Equal([session.SimulationSessionToken, ReconnectToken], fixture.GameService.PlayerDeaths.Select(death => death.SessionToken));
        Assert.Single(fixture.Core.CorpseStore.ListActive());
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // Another client of the account takes the session of a connected player over while an item
    // operation of the first client is on its way. GameService refuses the operation for the
    // token that the takeover rotated, and the refusal arrives before the takeover's join
    // registered the new token. The first client goes, but its entity stays as a body, which the
    // join resumes where it stands with its vitals, instead of a new entity at the place and
    // health of the last checkpoint.
    [Fact]
    public async Task ARefusalBeforeATakeoverRegisteredLeavesTheEntityForTheTakeoverToResume()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Taken Over Hero");
        fixture.Teleport(player, 3f, 4f, 90f);
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(player), 60, 40));
        var session = player.Context.Session!;
        var entityId = player.EntityId;
        var place = player.Entity.Movement.State;
        var itemGate = new TestGate();
        fixture.GameService.OnItemOperation = async (_, _, _) =>
        {
            await itemGate.WaitAsync();
            return RefusedItemCredentials();
        };
        var consumeGate = new TestGate();
        fixture.GameService.OnConsume = async (_, _) =>
        {
            await consumeGate.WaitAsync();
            return Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0);
        };
        fixture.SendItemIntent(player, RealtimeProtocolRejectionTests.CreateItemIntent());
        await WaitUntilAsync(() => itemGate.HasEntered, "the item operation to reach GameService");
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await WaitUntilAsync(() => consumeGate.HasEntered, "the takeover's ticket to reach GameService");

        itemGate.Release();
        await WaitUntilAsync(() => fixture.Core.Completions.Count == 1, "the refusal of the item operation");
        fixture.ProcessCompletions();
        Assert.Contains(player.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        RunTicks(fixture, fixture.TickRateHz / 10);
        Assert.True(fixture.Entities.TryGetPlayer(entityId, out _), "The refusal took the entity that the takeover goes on with.");

        consumeGate.Release();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        var accepted = JoinAccepted(peer);
        Assert.True(accepted.IsReconnect);
        Assert.Equal(entityId, accepted.ControlledEntityId);
        Assert.Equal((place.PositionX, place.PositionZ), (accepted.InitialPlayerState.PositionX, accepted.InitialPlayerState.PositionZ));
        Assert.True(fixture.Combat.TryGetPlayerCombatant(entityId, out var combatant));
        Assert.Equal((60, 40), (combatant.Health, combatant.Mana));
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The same takeover, but the refusal arrives after the join registered and resumed the
    // entity: it refused a token that the takeover replaced, so it ends nothing.
    [Fact]
    public async Task ARefusalAfterTheTakeoverRegisteredEndsNothing()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Taken Over Hero");
        fixture.Teleport(player, 3f, 4f, 90f);
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(player), 60, 40));
        var session = player.Context.Session!;
        var entityId = player.EntityId;
        var itemGate = new TestGate();
        fixture.GameService.OnItemOperation = async (_, _, _) =>
        {
            await itemGate.WaitAsync();
            return RefusedItemCredentials();
        };
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0));
        fixture.SendItemIntent(player, RealtimeProtocolRejectionTests.CreateItemIntent());
        await WaitUntilAsync(() => itemGate.HasEntered, "the item operation to reach GameService");
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await WaitUntilAsync(() => fixture.Core.Completions.Count == 1, "the takeover's join");
        fixture.ProcessCompletions();
        Assert.Equal(entityId, JoinAccepted(peer).ControlledEntityId);

        itemGate.Release();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        RunTicks(fixture, fixture.TickRateHz);

        Assert.True(fixture.Entities.TryGetPlayer(entityId, out _));
        Assert.True(fixture.Combat.TryGetPlayerCombatant(entityId, out var combatant));
        Assert.Equal((60, 40), (combatant.Health, combatant.Mana));
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.True(fixture.SessionStore.IsCurrent(current, DateTime.UtcNow));
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // GameService's answer to a release whose token a reconnect rotated.
    private static HttpResponseMessage RefusedCredentials()
    {
        return ScriptedGameServiceHandler.Problem(
            HttpStatusCode.Unauthorized,
            "invalid_simulation_session",
            "Simulation session credentials are invalid.");
    }

    // GameService's answer to an item transaction, such as a death, whose token a reconnect
    // rotated.
    private static HttpResponseMessage RefusedItemCredentials()
    {
        return ScriptedGameServiceHandler.Problem(
            HttpStatusCode.Unauthorized,
            "simulation_session_invalid",
            "The simulation session credentials or Shard binding are invalid.");
    }

    // GameService's answer to the reconnect's ticket: the same session with a new token, the item
    // state of the character, and the state it saved last.
    private static HttpResponseMessage Reconnect(
        CombatTickFixture fixture,
        ActiveSimulationSession session,
        float x,
        float z,
        int health,
        int mana,
        long itemStateRevision = 1)
    {
        return ScriptedGameServiceHandler.Json(new ConsumedSimulationJoinTicketResponse(
            session.AccountId,
            session.CharacterId,
            session.CharacterName,
            RealtimeServerHarness.ShardId,
            RealtimeServerHarness.WorldId,
            RealtimeServerHarness.WorkerId,
            RealtimeServerHarness.RuntimeId,
            session.SimulationSessionId,
            ReconnectToken,
            DateTime.UtcNow.AddMinutes(5),
            itemStateRevision,
            0,
            200,
            true)
        {
            SessionLeaseDurationMilliseconds = 150_000,
            Appearance = CharacterStateTestData.Appearance,
            TransportKey = RealtimeTestTickets.TransportKeyOf(ReconnectTicket),
            SavedState = new SimulationSavedCharacterState(
                RealtimeServerHarness.WorldId,
                fixture.Core.StaticCollisionWorld.Revision,
                x,
                0f,
                z,
                90f,
                health,
                mana)
        });
    }

    private static RealtimeJoinAccepted JoinAccepted(RecordingPeerConnection peer)
    {
        var packet = Assert.Single(peer.Sent, sent => sent.Type == RealtimeMessageType.JoinAccepted);
        Assert.True(RealtimeProtocol.TryDecodeJoinAccepted(packet.Data, out var accepted, out var error), error);
        return accepted;
    }

    private static void RunUntil(CombatTickFixture fixture, Func<bool> condition, string description, int? maximumTicks = null)
    {
        var limit = maximumTicks ?? (5 * fixture.TickRateHz);
        for (var tick = 0; tick < limit && !condition(); tick++)
        {
            fixture.RunTick();
        }

        Assert.True(condition(), $"Waited {limit} ticks for {description}.");
    }

    private static void RunTicks(CombatTickFixture fixture, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            fixture.RunTick();
        }
    }

    // Runs the loop as it runs with GameService's answers: every tick, the calls it started are
    // answered and their completions processed.
    private static async Task RunUntilAsync(CombatTickFixture fixture, Func<bool> condition, string description)
    {
        var limit = 5 * fixture.TickRateHz;
        for (var tick = 0; tick < limit && !condition(); tick++)
        {
            fixture.RunTick();
            await fixture.WaitForOperationsAsync();
            fixture.ProcessCompletions();
        }

        Assert.True(condition(), $"Waited {limit} ticks for {description}.");
    }

    // For what happens off the loop, such as a GameService call that started.
    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + RealtimeServerHarness.PollTimeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), $"Waited for {description}.");
    }
}
