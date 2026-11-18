using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using static Mmorpg.SimulationWorker.Tests.Realtime.LingeringBodyReconnectOrderTests;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A player who died is saved as its respawn leaves it, at the World spawn with full vitals, only
// once GameService committed the death, with its corpse and the items on it. Until then the state
// that GameService holds stays the last one the character had alive: a checkpoint says nothing
// new, an orderly stop gives the death one more attempt with the operation id of every retry, and
// a stop or a crash that cannot commit it returns the character alive where it last was alive,
// with its items.
public sealed class PendingDeathSaveTests
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(1);

    // The checkpoint finds the player dead while the death waits behind a call that has not
    // answered: nothing is saved for it. Once the death committed, the next checkpoint saves the
    // respawn.
    [Fact]
    public async Task ACheckpointWhileADeathWaitsKeepsTheLastLivingStateAndTheCommitSavesTheRespawn()
    {
        using var fixture = Create(out var killer, out var victim);
        var session = victim.Context.Session!;
        var deathGate = new TestGate();
        fixture.GameService.OnPlayerDeath = async (_, request, _) =>
        {
            await deathGate.WaitAsync();
            return ScriptedGameServiceHandler.DefaultPlayerDeath(request);
        };
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 1, "the first checkpoint");
        var living = SavedStates(fixture, session).Single();
        Assert.Equal((3f, 4f, 100, 100), (living.PositionX, living.PositionZ, living.Health, living.Mana));

        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victim.EntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the victim");
        await WaitUntilAsync(() => deathGate.HasEntered, "the death to reach GameService");
        await RunWithoutWaitingAsync(fixture, 4 * fixture.TickRateHz);

        // Several checkpoints came due while the death waited, and none said anything for the victim.
        Assert.Single(SavedStates(fixture, session));
        Assert.True(fixture.GameService.Checkpoints.Count >= 2);

        deathGate.Release();
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 2, "the checkpoint after the commit");
        var respawned = SavedStates(fixture, session)[1];
        Assert.Equal((0f, -1f, 100, 100), (respawned.PositionX, respawned.PositionZ, respawned.Health, respawned.Mana));
        Assert.True(respawned.Sequence > living.Sequence);
        Assert.Equal(0, fixture.LoopGuard.TotalFailures);
    }

    // A death that was in flight when the worker stopped committed in the meantime: its answer
    // arrived and was never finalized, so the stop settles it and saves the respawn in its last
    // checkpoint.
    [Fact]
    public async Task AStopWithADeathHeldBehindAGateThatThenOpensSavesTheRespawn()
    {
        using var fixture = Create(out var killer, out var victim);
        var session = victim.Context.Session!;
        var deathGate = new TestGate();
        fixture.GameService.OnPlayerDeath = async (_, request, _) =>
        {
            await deathGate.WaitAsync();
            return ScriptedGameServiceHandler.DefaultPlayerDeath(request);
        };
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 1, "the first checkpoint");
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victim.EntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the victim");
        await WaitUntilAsync(() => deathGate.HasEntered, "the death to reach GameService");

        var stop = fixture.RunShutdownStepsAsync();
        deathGate.Release();
        await stop;

        var respawned = SavedStates(fixture, session)[^1];
        Assert.Equal(2, SavedStates(fixture, session).Count);
        Assert.Equal((0f, -1f, 100, 100), (respawned.PositionX, respawned.PositionZ, respawned.Health, respawned.Mana));
        Assert.Single(fixture.GameService.PlayerDeaths);
    }

    // The death waits for a retry after an answer that left its outcome open. The stop gives it
    // one more attempt, with the operation id that every retry uses, and it commits.
    [Fact]
    public async Task AStopGivesAWaitingDeathOneMoreAttemptWithTheSameOperationIdAndSavesTheRespawn()
    {
        using var fixture = Create(out var killer, out var victim);
        var session = victim.Context.Session!;
        var attempts = 0;
        fixture.GameService.OnPlayerDeath = (_, request, _) => Task.FromResult(
            Interlocked.Increment(ref attempts) == 1
                ? ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable.")
                : ScriptedGameServiceHandler.DefaultPlayerDeath(request));
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 1, "the first checkpoint");
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victim.EntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the victim");
        fixture.RunTick();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        Assert.Single(fixture.GameService.PlayerDeaths);

        await fixture.RunShutdownStepsAsync();

        var deaths = fixture.GameService.PlayerDeaths;
        Assert.Equal(2, deaths.Count);
        Assert.Equal(deaths[0].DeathEventId, deaths[1].DeathEventId);
        Assert.Equal(deaths[0].OperationId, deaths[1].OperationId);
        var respawned = SavedStates(fixture, session)[^1];
        Assert.Equal(2, SavedStates(fixture, session).Count);
        Assert.Equal((0f, -1f, 100, 100), (respawned.PositionX, respawned.PositionZ, respawned.Health, respawned.Mana));
    }

    // The stop's attempt fails too: GameService answers, but the death does not commit. Nothing
    // says that the character respawned, so GameService keeps the last living state, and no final
    // state goes with a release.
    [Fact]
    public async Task AStopWhoseLastAttemptAtTheDeathFailsLeavesTheLastLivingState()
    {
        using var fixture = Create(out var killer, out var victim);
        var session = victim.Context.Session!;
        fixture.GameService.OnPlayerDeath = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable."));
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 1, "the first checkpoint");
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victim.EntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the victim");
        fixture.RunTick();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();

        await fixture.RunShutdownStepsAsync();

        Assert.Equal(2, fixture.GameService.PlayerDeaths.Count);
        Assert.Single(SavedStates(fixture, session));
        Assert.False(fixture.ReleaseService.FinalStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
    }

    // GameService does not answer when the worker stops, so the stop sends nothing: the death is
    // lost, and the state that GameService holds is the last one that the character had alive.
    [Fact]
    public async Task AStopWhileGameServiceDoesNotAnswerSavesNothingForAPendingDeath()
    {
        using var fixture = Create(out var killer, out var victim);
        var session = victim.Context.Session!;
        fixture.GameService.OnPlayerDeath = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable."));
        await RunUntilAsync(fixture, () => SavedStates(fixture, session).Count == 1, "the first checkpoint");
        fixture.SendCombatIntent(killer, RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victim.EntityId));
        RunUntil(fixture, () => !fixture.CombatantOf(victim).IsAlive, "the bolt to kill the victim");
        fixture.RunTick();
        await fixture.WaitForOperationsAsync();
        fixture.ProcessCompletions();
        fixture.Availability.HeartbeatFailed("auth_service_unavailable");
        var checkpoints = fixture.GameService.Checkpoints.Count;

        await fixture.RunShutdownStepsAsync();

        Assert.Single(fixture.GameService.PlayerDeaths);
        Assert.Equal(checkpoints, fixture.GameService.Checkpoints.Count);
        Assert.Single(SavedStates(fixture, session));
        Assert.False(fixture.ReleaseService.FinalStates.TryGet(session.CharacterId, session.SimulationSessionId, out _));
    }

    private static CombatTickFixture Create(out CombatTickPlayer killer, out CombatTickPlayer victim)
    {
        var fixture = new CombatTickFixture(
            TestRules(),
            TestCatalog(minimumDamage: 500, maximumDamage: 500),
            sessions: true,
            checkpoints: true,
            checkpointInterval: CheckpointInterval);
        killer = fixture.Join(1, "Death Killer", [.. CasterEquipment()]);
        victim = fixture.Join(2, "Death Victim");
        fixture.Teleport(victim, 3f, 4f, 90f);
        fixture.ShowEveryoneToEveryone();
        return fixture;
    }

    // Every state of the session that the worker sent in a checkpoint, in the order it sent them.
    private static List<SimulationCharacterCheckpointEntry> SavedStates(CombatTickFixture fixture, Sessions.ActiveSimulationSession session)
    {
        return [.. fixture.GameService.Checkpoints
            .SelectMany(checkpoint => checkpoint.States)
            .Where(state => state.SimulationSessionId == session.SimulationSessionId)];
    }

    // Runs ticks while a call that the test holds stays unanswered; the completions of the other
    // calls, such as a checkpoint, are finalized as they come.
    private static async Task RunWithoutWaitingAsync(CombatTickFixture fixture, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            fixture.RunTick();
            await Task.Delay(2);
            fixture.ProcessCompletions();
        }
    }
}
