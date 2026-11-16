using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The orphan sweep releases a session that neither a peer nor a body owns once twice the join
// handshake timeout passed, 20 seconds in production and 600 ms here. A session whose body went
// while its spent arrows had not committed belongs to the combat authority, which releases it
// once they did. That work waits while GameService does not answer, which the worker tolerates
// for its outage tolerance, so the sweep leaves such a session through an outage and releases it
// only once the work stayed pending for the outage tolerance of the time GameService answered.
public sealed class OrphanSweepPendingWorkTests
{
    private static readonly TimeSpan JoinHandshakeTimeout = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task AnOutageLongerThanTheSweepLeavesASessionWhoseArrowsWaitAndTheyCommitAfterIt()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = Create(clock);
        var (session, plainOrphan) = DeferredSessionWithUncommittedArrows(fixture);

        // GameService does not answer for longer than the sweep waits for an orphan.
        fixture.Availability.HeartbeatFailed("auth_service_unavailable");
        var outageEnds = DateTime.UtcNow + (JoinHandshakeTimeout * 2) + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < outageEnds)
        {
            fixture.RunTick();
            fixture.SweepOrphans();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(fixture.SessionStore.TryGet(plainOrphan.CharacterId, out _));
        Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out _), "The sweep released a session whose arrows wait for GameService.");
        Assert.True(fixture.PlayerCombat.HoldsDurableWork(session.SimulationSessionId));
        Assert.Empty(fixture.GameService.AmmunitionConsumptions);

        // GameService answers again: the arrows commit, and the combat authority releases the
        // session after them.
        fixture.Availability.HeartbeatSucceeded();
        for (var tick = 0; tick < fixture.TickRateHz && fixture.SessionStore.TryGet(session.CharacterId, out _); tick++)
        {
            fixture.RunTick();
            await fixture.WaitForOperationsAsync();
            fixture.ProcessCompletions();
            fixture.SweepOrphans();
        }

        await fixture.WaitForOperationsAsync();
        Assert.False(fixture.SessionStore.TryGet(session.CharacterId, out _));
        Assert.Single(fixture.GameService.AmmunitionConsumptions);
        var calls = fixture.GameService.Calls;
        var consumed = IndexOf(calls, $"/api/simulation-sessions/{session.SimulationSessionId}/ammunition-consumptions");
        var released = IndexOf(calls, $"/api/simulation-sessions/{session.SimulationSessionId}/release");
        Assert.True(consumed >= 0 && released > consumed, "The session went before its arrows committed.");
        Assert.Single(fixture.GameService.Releases, release => release.SimulationSessionId == session.SimulationSessionId);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // While GameService answers but the work stays pending, the sweep releases the session once
    // the work stayed pending for the retry horizon, as long as the work itself is retried, and
    // not before.
    [Fact]
    public async Task ASessionWhoseArrowsStayPendingWhileGameServiceAnswersGoesAfterTheRetryHorizon()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = Create(clock);
        var gate = new TestGate();
        fixture.GameService.OnAmmunitionConsumption = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return fixture.GameService.DefaultAmmunitionConsumption(request);
        };
        var (session, _) = DeferredSessionWithUncommittedArrows(fixture);
        try
        {
            var sweepEnds = DateTime.UtcNow + (JoinHandshakeTimeout * 2) + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < sweepEnds)
            {
                fixture.RunTick();
                fixture.SweepOrphans();
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.True(gate.HasEntered);
            Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out _));

            Assert.True(fixture.Config.UncertainOperationRetryHorizon > fixture.Config.GameServiceOutageTolerance);
            clock.Advance(fixture.Config.GameServiceOutageTolerance);
            fixture.SweepOrphans();
            Assert.True(fixture.SessionStore.TryGet(session.CharacterId, out _));

            clock.Advance(fixture.Config.UncertainOperationRetryHorizon - fixture.Config.GameServiceOutageTolerance);
            var sweepAgain = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < sweepAgain && fixture.SessionStore.TryGet(session.CharacterId, out _))
            {
                fixture.SweepOrphans();
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(fixture.SessionStore.TryGet(session.CharacterId, out _));
        }
        finally
        {
            gate.Release();
            await fixture.WaitForOperationsAsync();
        }
    }

    private static CombatTickFixture Create(SteppedTimeProvider clock)
    {
        return new CombatTickFixture(
            TestRules(),
            TestCatalog(minimumDamage: 1, maximumDamage: 1),
            sessions: true,
            availabilityClock: clock,
            joinHandshakeTimeout: JoinHandshakeTimeout);
    }

    // An archer shoots, so its arrows wait for their batch, and its body then goes with them still
    // uncommitted, as the body timer lets a body go whose time is up: the combat authority keeps
    // the session for the arrows. Next to it a session that nobody owns and that holds no work.
    private static (ActiveSimulationSession Session, ActiveSimulationSession PlainOrphan) DeferredSessionWithUncommittedArrows(
        CombatTickFixture fixture)
    {
        var archer = fixture.Join(1, "Late Archer", [.. ArcherEquipment(50)]);
        var target = fixture.Join(2, "Patient Target");
        fixture.ShowEveryoneToEveryone();
        var session = archer.Context.Session!;
        fixture.SendCombatIntent(archer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), target.EntityId));
        for (var tick = 0; tick < fixture.TickRateHz && !HasPendingShots(fixture, session); tick++)
        {
            fixture.RunTick();
        }

        Assert.True(HasPendingShots(fixture, session));
        fixture.SendCombatIntent(archer, RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        var plainOrphan = RealtimeTestSessions.Create("Plain Orphan");
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, fixture.SessionStore.Register(plainOrphan));

        fixture.Leave(archer);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));
        Assert.True(fixture.PlayerCombat.TryDeferRelease(body.Session));
        fixture.Core.Teardown.RemoveBody(body, LingeringBodyTimer.TimeUpReason);
        Assert.False(fixture.Entities.TryGetPlayer(archer.EntityId, out _));
        return (session, plainOrphan);
    }

    private static bool HasPendingShots(CombatTickFixture fixture, ActiveSimulationSession session)
    {
        return fixture.PlayerCombat.TryGet(session.SimulationSessionId, out var combatSession)
            && combatSession.PendingShots > 0;
    }

    private static int IndexOf(IReadOnlyList<string> calls, string path)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            if (calls[index] == path)
            {
                return index;
            }
        }

        return -1;
    }
}
