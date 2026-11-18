using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.LingeringBodyReconnectOrderTests;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A join that consumed its ticket and registered the session's new generation, whose peer went
// before the loop bound it. The consumption rotated the token at GameService, so the player who is
// connected under the old generation has lost its token, whatever happened to the join. The
// session must go on for what the worker holds of it, and only a session that nothing holds is
// released.
public sealed class AbortedTakeoverTests
{
    // Another client of the account took the session over, and aborted before its join was bound.
    // The connected player was not removed from its entity or its session: it is told that its
    // generation was replaced, its entity stands as a body that it can come back to, and the body
    // goes after its time with a release that carries the token GameService holds.
    [Fact]
    public async Task ATakeoverThatNeverBoundLeavesTheConnectedPlayerItsSessionAndABody()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Playing Hero");
        fixture.Teleport(player, 3f, 4f, 90f);
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(player), 60, 40));
        var session = player.Context.Session!;
        var entityId = player.EntityId;
        var place = player.Entity.Movement.State;
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0));

        // GameService consumed the ticket of the other client and rotated the token, and the
        // worker registered the new generation. The other client went before the loop bound it.
        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.Directory.Peers.Remove(3);
        fixture.ProcessCompletions();
        await fixture.WaitForOperationsAsync();

        Assert.Empty(fixture.GameService.Releases);
        Assert.True(fixture.Entities.TryGetPlayer(entityId, out var entity));
        Assert.Equal(ReconnectToken, entity!.Session.SimulationSessionToken);
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.True(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
        Assert.DoesNotContain(player.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);

        fixture.EnforcePeerState();

        // The old generation is not current any more, and a newer one holds the session, so the
        // player's connection ends as a takeover and its entity stays.
        var disconnect = Assert.Single(player.Peer.Sent, sent => sent.Type == RealtimeMessageType.ServerDisconnect);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(disconnect.Data, out var reason, out var error), error);
        Assert.Equal("session_reconnected", reason.Code);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));
        Assert.Equal(entityId, body.Entity.NetworkEntityId);
        Assert.Equal(ReconnectToken, body.Session.SimulationSessionToken);
        Assert.Empty(fixture.GameService.Releases);

        // The body keeps its time, which a body that waited for a join that ended would not, and
        // then the session is released with the token that GameService holds and the body's vitals.
        RunTicks(fixture, (fixture.Config.LingeringBodySeconds * fixture.TickRateHz) + 2);
        await fixture.WaitForOperationsAsync();
        Assert.False(fixture.Entities.TryGetPlayer(entityId, out _));
        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal(ReconnectToken, release.Request.SessionToken);
        Assert.Equal((place.PositionX, place.PositionZ), (release.Request.FinalState!.PositionX, release.Request.FinalState.PositionZ));
        Assert.Equal((60, 40), (release.Request.FinalState.Health, release.Request.FinalState.Mana));
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The player is still connected when the aborted join ends, and then it plays on: the
    // supervisor took its connection only because its generation was replaced, so a player that
    // comes back with a ticket within the body's time resumes the entity where it stands.
    [Fact]
    public async Task APlayerWhoseTakeoverNeverBoundResumesItsBodyWithANewTicket()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Returning Hero");
        fixture.Teleport(player, 3f, 4f, 90f);
        Assert.True(fixture.Combat.TryRestoreVitals(fixture.CombatantOf(player), 60, 40));
        var session = player.Context.Session!;
        var entityId = player.EntityId;
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, -2f, 1f, health: 100, mana: 100, itemStateRevision: 0));
        var (aborted, abortedPeer) = fixture.Connect(3);
        fixture.RequestJoin(aborted, abortedPeer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.Directory.Peers.Remove(3);
        fixture.ProcessCompletions();
        fixture.EnforcePeerState();
        RunTicks(fixture, fixture.TickRateHz);

        var (context, peer) = fixture.Connect(4);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        var accepted = JoinAccepted(peer);
        Assert.True(accepted.IsReconnect);
        Assert.Equal(entityId, accepted.ControlledEntityId);
        Assert.True(fixture.Combat.TryGetPlayerCombatant(entityId, out var combatant));
        Assert.Equal((60, 40), (combatant.Health, combatant.Mana));
        Assert.False(fixture.Core.Bodies.TryGet(session.CharacterId, out _));
        Assert.Empty(fixture.GameService.Releases);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // Nothing of the session is held on the worker, so the join that ended without a peer
    // releases it, as before.
    [Fact]
    public async Task AnAbortedJoinOfASessionThatNothingHoldsReleasesIt()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var other = RealtimeTestSessions.Create("Nobody Holds Me");
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, other, 0f, -1f, health: 100, mana: 100, itemStateRevision: 0));

        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.Directory.Peers.Remove(3);
        fixture.ProcessCompletions();
        await fixture.WaitForOperationsAsync();

        var release = Assert.Single(fixture.GameService.Releases);
        Assert.Equal(other.SimulationSessionId, release.SimulationSessionId);
        Assert.Equal(ReconnectToken, release.Request.SessionToken);
        Assert.False(fixture.SessionStore.TryGet(other.CharacterId, out _));
    }

    // The entity went while the spent arrows of the session had not committed, so the combat
    // authority holds the session until they did. The join that ended without a peer must not
    // release it under that work: the work goes on with the new generation's token.
    [Fact]
    public async Task AnAbortedJoinOfASessionWhoseCombatWorkHasNotCommittedKeepsTheSessionForTheWork()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Unfinished Hero");
        var session = player.Context.Session!;
        fixture.Leave(player);
        Assert.True(fixture.PlayerCombat.TryGet(session.SimulationSessionId, out var combatSession));
        combatSession.PendingShots = 1;
        fixture.Core.Bodies.TryGet(session.CharacterId, out var body);
        Assert.True(fixture.PlayerCombat.TryDeferRelease(session));
        fixture.Core.Teardown.RemoveBody(body, "left_shard");
        Assert.False(fixture.Entities.TryGetPlayer(player.EntityId, out _));
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, 0f, -1f, health: 100, mana: 100, itemStateRevision: 0));

        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.Directory.Peers.Remove(3);
        fixture.ProcessCompletions();
        await fixture.WaitForOperationsAsync();

        Assert.Empty(fixture.GameService.Releases);
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.Equal(ReconnectToken, combatSession.Session.SimulationSessionToken);
    }
}
