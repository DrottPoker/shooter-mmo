using System.Diagnostics.Metrics;
using System.Net;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.LingeringBodyReconnectOrderTests;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// An operator sees presence: the bodies in the world now, how they end, what a reconnect did with
// the entity, what became of the checkpoints, the session leases and releases, and joins that
// lost a session. The instrument names are part of the documented contract, and each count
// happens where the event does.
public sealed class PresenceMetricsTests
{
    [Fact]
    public void ThePresenceMeterPublishesTheDocumentedInstrumentNames()
    {
        var published = new SortedSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == PresenceMetrics.MeterName)
                {
                    lock (published)
                    {
                        published.Add(instrument.Name);
                    }
                }
            }
        };
        listener.Start();

        using var metrics = new PresenceMetrics();

        Assert.Equal(
            [
                "simulation_worker.presence.bodies_ended",
                "simulation_worker.presence.checkpoint_oldest_unsaved_age",
                "simulation_worker.presence.checkpoint_states",
                "simulation_worker.presence.joins_refused",
                "simulation_worker.presence.lingering_bodies",
                "simulation_worker.presence.reconnects",
                "simulation_worker.presence.releases",
                "simulation_worker.presence.session_heartbeat_rounds",
                "simulation_worker.presence.sessions_ended_locally"
            ],
            published);
    }

    // A body counts while it stands and as the reason it ended: its time ran out, here.
    [Fact]
    public void ABodyIsCountedWhileItStandsAndWhenItsTimeRunsOut()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var player = fixture.Join(1, "Standing Body");
        Assert.Equal(0, counts.Gauge("simulation_worker.presence.lingering_bodies"));

        fixture.Leave(player);
        Assert.Equal(1, counts.Gauge("simulation_worker.presence.lingering_bodies"));
        RunTicks(fixture, (fixture.Config.LingeringBodySeconds * fixture.TickRateHz) + 2);

        Assert.Equal(0, counts.Gauge("simulation_worker.presence.lingering_bodies"));
        Assert.Equal(1, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodyTimeUp));
    }

    [Fact]
    public async Task ABodyThatDiedIsCountedAsDied()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(minimumDamage: 500, maximumDamage: 500), sessions: true);
        using var counts = new PresenceMetricsListener();
        var killer = fixture.Join(1, "Body Killer", [.. CasterEquipment()]);
        var victim = fixture.Join(2, "Fallen Body");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        var entityId = victim.EntityId;
        fixture.Leave(victim);

        fixture.SendCombatIntent(killer, Mmorpg.GameProtocol.RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), entityId));
        await RunUntilAsync(fixture, () => !fixture.Entities.TryGetPlayer(entityId, out _), "the body to go");

        Assert.Equal(1, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodyDied));
        Assert.Equal(0, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodyTimeUp));
    }

    [Fact]
    public void ABodyWhoseSessionEndedIsCountedAsEndedWithTheSession()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var player = fixture.Join(1, "Revoked Body");
        var session = player.Context.Session!;
        fixture.Leave(player);

        Assert.True(fixture.SessionStore.Invalidate(
            session.CharacterId,
            session.SimulationSessionId,
            session.SimulationSessionToken,
            "session_revoked",
            "The session ended."));
        RunTicks(fixture, 2);

        Assert.Equal(1, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodySessionEnded));
        Assert.Equal(1, counts.Of("simulation_worker.presence.sessions_ended_locally", PresenceMetrics.SessionInvalidated));
    }

    // A reconnect that found the body resumes it.
    [Fact]
    public async Task AReconnectThatFindsTheBodyIsCountedAsResumingIt()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var player = fixture.Join(1, "Coming Back");
        fixture.Teleport(player, 3f, 4f, 90f);
        var session = player.Context.Session!;
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, 3f, 4f, 100, 100, 0));
        fixture.Leave(player);

        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        Assert.Equal(1, counts.Of("simulation_worker.presence.reconnects", PresenceMetrics.ReconnectResumed));
        Assert.Equal(0, counts.Of("simulation_worker.presence.reconnects", PresenceMetrics.ReconnectNewEntity));
        Assert.Equal(1, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodyResumed));
        Assert.Equal(0, counts.Gauge("simulation_worker.presence.lingering_bodies"));
    }

    // A reconnect that came after the body went places a new entity from the state that the
    // session kept.
    [Fact]
    public async Task AReconnectAfterTheBodyWentIsCountedAsPlacingANewEntity()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var player = fixture.Join(1, "Late Comer");
        fixture.Teleport(player, 3f, 4f, 90f);
        var session = player.Context.Session!;
        fixture.GameService.OnConsume = (_, _) => Task.FromResult(
            Reconnect(fixture, session, 3f, 4f, 100, 100, 0));
        fixture.GameService.OnRelease = (_, _, _) => Task.FromResult(RefusedCredentials());
        fixture.Leave(player);
        Assert.True(fixture.Core.Bodies.TryGet(session.CharacterId, out var body));
        fixture.Core.Teardown.RemoveBody(body, "left_shard");

        var (context, peer) = fixture.Connect(3);
        fixture.RequestJoin(context, peer, ReconnectTicket);
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        Assert.Equal(1, counts.Of("simulation_worker.presence.reconnects", PresenceMetrics.ReconnectNewEntity));
        Assert.Equal(0, counts.Of("simulation_worker.presence.reconnects", PresenceMetrics.ReconnectResumed));
    }

    // The orderly stop takes every body out with the world.
    [Fact]
    public async Task BodiesThatTheStopTakesAreCountedAsStopped()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        fixture.Leave(fixture.Join(1, "Stopped Body"));
        fixture.Leave(fixture.Join(2, "Other Stopped Body"));

        await fixture.RunShutdownStepsAsync();

        Assert.Equal(2, counts.Of("simulation_worker.presence.bodies_ended", PresenceMetrics.BodyWorkerStopped));
    }

    // A session whose lease ran out counts as expired when the store ends it.
    [Fact]
    public void ASessionWhoseLeaseRanOutIsCountedAsExpired()
    {
        using var metrics = new PresenceMetrics();
        using var counts = new PresenceMetricsListener();
        var time = new SteppedTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var store = new ActiveSimulationSessionStore(time, metrics);
        var session = RealtimeTestSessions.Create("Expiring Hero") with
        {
            Lease = new SessionLease(time.GetTimestamp(), TimeSpan.FromSeconds(30))
        };
        store.Register(session);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(store.IsCurrent(session));

        Assert.Equal(1, counts.Of("simulation_worker.presence.sessions_ended_locally", PresenceMetrics.SessionLeaseExpired));
    }

    // The age of the oldest state that GameService has not confirmed grows while checkpoints fail,
    // and a checkpoint that got through counts every state as sent and saved and clears the age.
    [Fact]
    public async Task CheckpointStatesAreCountedAndTheOldestUnsavedAgeGrowsWhileTheyFail()
    {
        using var fixture = new CombatTickFixture(
            TestRules(),
            TestCatalog(),
            sessions: true,
            checkpoints: true,
            checkpointInterval: TimeSpan.FromSeconds(1));
        using var counts = new PresenceMetricsListener();
        fixture.Join(1, "Saved Hero");
        fixture.Join(2, "Other Saved Hero");
        var attempts = 0;
        fixture.GameService.OnCheckpoint = (request, _) => Task.FromResult(
            Interlocked.Increment(ref attempts) == 1
                ? ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable.")
                : ScriptedGameServiceHandler.Json(new Mmorpg.SimulationWorker.GameServiceApi.SimulationCharacterCheckpointResponse(request.States.Count - 1, 1)));

        await RunUntilAsync(fixture, () => attempts == 1, "the failing checkpoint");
        Assert.NotNull(counts.Gauge("simulation_worker.presence.checkpoint_oldest_unsaved_age"));
        Assert.Equal(2, counts.Of("simulation_worker.presence.checkpoint_states", PresenceMetrics.CheckpointFailed));

        await RunUntilAsync(fixture, () => attempts == 2, "the checkpoint that goes through");

        Assert.Equal(4, counts.Of("simulation_worker.presence.checkpoint_states", PresenceMetrics.CheckpointSent));
        Assert.Equal(1, counts.Of("simulation_worker.presence.checkpoint_states", PresenceMetrics.CheckpointSaved));
        Assert.Equal(1, counts.Of("simulation_worker.presence.checkpoint_states", PresenceMetrics.CheckpointSkipped));
        Assert.Null(counts.Gauge("simulation_worker.presence.checkpoint_oldest_unsaved_age"));
    }

    // The release of a session counts as released, failed, or given up when the worker stops first.
    [Fact]
    public async Task ReleasesAreCountedAsReleasedFailedOrGivenUp()
    {
        using var fixture = new CombatTickFixture(TestRules(), TestCatalog(), sessions: true);
        using var counts = new PresenceMetricsListener();
        var released = fixture.Join(1, "Released Hero").Context.Session!;
        var failed = fixture.Join(2, "Failed Hero").Context.Session!;
        fixture.GameService.OnRelease = (sessionId, _, _) => Task.FromResult(
            sessionId == failed.SimulationSessionId
                ? ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable.")
                : fixture.GameService.DefaultRelease(sessionId));

        await fixture.ReleaseService.ReleaseDetachedAsync(released, CancellationToken.None);
        await fixture.ReleaseService.ReleaseDetachedAsync(failed, CancellationToken.None);
        using var stopped = new CancellationTokenSource();
        fixture.Availability.HeartbeatFailed("auth_service_unavailable");
        await stopped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.ReleaseService.ReleaseDetachedAsync(RealtimeTestSessions.Create("Stopped Hero"), stopped.Token));

        Assert.Equal(1, counts.Of("simulation_worker.presence.releases", PresenceMetrics.ReleaseReleased));
        Assert.Equal(1, counts.Of("simulation_worker.presence.releases", PresenceMetrics.ReleaseFailed));
        Assert.Equal(1, counts.Of("simulation_worker.presence.releases", PresenceMetrics.ReleaseGaveUp));
    }
}
