using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.LingeringBodyReconnectOrderTests;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Generations of one session have an order, and the end of an older generation never takes what a
// newer one registered. Two joins of the same session may consume their tickets at GameService in
// one order and register on the worker in the other, and a body's end may meet the registration
// of the reconnect that comes for it in the same instant. The stores are plain objects, so every
// test calls them in the order that lost before.
public sealed class SessionGenerationOrderTests
{
    private const string FirstTicket = "ticket-of-the-first-join";
    private const string SecondTicket = "ticket-of-the-second-join";

    // The first join consumed its ticket first, so GameService holds the token of the second
    // one; the second registers here first. When the first registers after it, it must not
    // replace the second's token: the next heartbeat would be refused, and both clients drop.
    [Fact]
    public async Task AJoinThatConsumedFirstAndRegistersLastIsRefusedAndTheSecondKeepsTheSession()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var player = fixture.Join(1, "Contested Hero");
        var session = player.Context.Session!;
        var firstGate = new TestGate();
        fixture.GameService.OnConsume = async (request, _) =>
        {
            if (request.TicketId == RealtimeTestTickets.IdOf(FirstTicket))
            {
                await firstGate.WaitAsync();
                return Reconnect(fixture, session, 0f, -1f, 100, 100, 0, FirstTicket, "token-of-the-first-join", generation: 2);
            }

            return Reconnect(fixture, session, 0f, -1f, 100, 100, 0, SecondTicket, "token-of-the-second-join", generation: 3);
        };
        var (first, firstPeer) = fixture.Connect(3);
        fixture.RequestJoin(first, firstPeer, FirstTicket);
        await WaitUntilAsync(() => firstGate.HasEntered, "the first ticket to reach GameService");
        var (second, secondPeer) = fixture.Connect(4);
        fixture.RequestJoin(second, secondPeer, SecondTicket);
        await WaitUntilAsync(() => fixture.Core.Completions.Count == 1, "the second join");
        fixture.ProcessCompletions();
        Assert.Equal(player.EntityId, JoinAccepted(secondPeer).ControlledEntityId);

        firstGate.Release();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        // The first join holds no keys, so its refusal travels as the data of its disconnect.
        var disconnect = first.PendingDisconnect;
        Assert.NotNull(disconnect);
        Assert.True(RealtimeProtocol.TryDecodeJoinRejected(disconnect.Data!, out var error, out var decodeError), decodeError);
        Assert.Equal("simulation_session_changed", error.Code);
        Assert.DoesNotContain(firstPeer.Sent, sent => sent.Type == RealtimeMessageType.JoinAccepted);
        Assert.Equal(1, counts.Of("simulation_worker.presence.joins_refused", "stale_generation"));
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal("token-of-the-second-join", current!.SimulationSessionToken);
        Assert.Equal(3, current.Generation);
        Assert.True(fixture.Entities.TryGetPlayer(player.EntityId, out _));
        Assert.True(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
        Assert.Empty(fixture.GameService.Releases);
        Assert.DoesNotContain(secondPeer.Sent, sent => sent.Type is RealtimeMessageType.ServerDisconnect or RealtimeMessageType.JoinRejected);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The orders GameService's answers can arrive in are both fine when the order of the
    // consumption decides: the join that consumed last holds the session.
    [Fact]
    public async Task TheJoinThatConsumedLastHoldsTheSessionWhateverOrderTheAnswersArriveIn()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Ordered Hero");
        var session = player.Context.Session!;
        fixture.GameService.OnConsume = (request, _) => Task.FromResult(
            request.TicketId == RealtimeTestTickets.IdOf(FirstTicket)
                ? Reconnect(fixture, session, 0f, -1f, 100, 100, 0, FirstTicket, "token-of-the-first-join", generation: 2)
                : Reconnect(fixture, session, 0f, -1f, 100, 100, 0, SecondTicket, "token-of-the-second-join", generation: 3));
        var (first, firstPeer) = fixture.Connect(3);
        var (second, secondPeer) = fixture.Connect(4);

        // The first one registers before the second: both complete, and the second replaces it.
        fixture.RequestJoin(first, firstPeer, FirstTicket);
        await fixture.WaitForOperationsAsync();
        fixture.RequestJoin(second, secondPeer, SecondTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal("token-of-the-second-join", current!.SimulationSessionToken);
        Assert.Contains(secondPeer.Sent, sent => sent.Type == RealtimeMessageType.JoinAccepted);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The body's end took the per-session state of its generation by session id, and a reconnect
    // that registered a newer generation right before found none, so it released a session that
    // was not over and answered carry_state_missing: a fourth outcome. The end takes the state of
    // the generation that ends only.
    [Fact]
    public async Task ABodyThatEndsAfterAReconnectRegisteredLeavesTheStateForTheReconnect()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Ended Body");
        fixture.Teleport(player, 3f, 4f, 90f);
        var session = player.Context.Session!;
        fixture.Leave(player);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, 3f, 4f, 100, 100, 0, generation: 2));
        var (context, peer) = fixture.Connect(3);

        // The join consumed its ticket and registered the new generation; the loop did not
        // finalize it yet when the body ends by another path than the timer, which waits for it.
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.Core.Teardown.RemoveBody(body, "session_revoked");
        Assert.False(fixture.Entities.TryGetPlayer(player.EntityId, out _));
        Assert.True(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
        fixture.ProcessCompletions();
        await fixture.WaitForOperationsAsync();

        // The join gives the player a new entity where the body stood, and the session goes on.
        var accepted = JoinAccepted(peer);
        Assert.True(accepted.IsReconnect);
        Assert.NotEqual(player.EntityId, accepted.ControlledEntityId);
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.JoinRejected);
        Assert.Empty(fixture.GameService.Releases);
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out var current));
        Assert.Equal(ReconnectToken, current!.SimulationSessionToken);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The other order: the body's end takes its state before the reconnect registered, and the
    // join stores the state of the new generation, so the join finds it.
    [Fact]
    public async Task ABodyThatEndsBeforeAReconnectRegistersLeavesTheReconnectItsOwnState()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Earlier Body");
        fixture.Teleport(player, 3f, 4f, 90f);
        var session = player.Context.Session!;
        fixture.Leave(player);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));
        fixture.Core.Teardown.RemoveBody(body, "session_revoked");
        Assert.False(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, 3f, 4f, 100, 100, 0, generation: 2));
        var (context, peer) = fixture.Connect(3);

        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        Assert.True(JoinAccepted(peer).IsReconnect);
        Assert.DoesNotContain(peer.Sent, sent => sent.Type == RealtimeMessageType.JoinRejected);
        Assert.True(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // The state of an entity that ends without a newer generation still goes with it.
    [Fact]
    public void TheStateOfTheGenerationThatEndsGoesWithIt()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        var player = fixture.Join(1, "Gone Body");
        var session = player.Context.Session!;
        fixture.Leave(player);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));

        fixture.Core.Teardown.RemoveBody(body, "left_shard");

        Assert.False(fixture.ItemStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
    }
}
