using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.WorldData.Items;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A character's place and vitals over real UDP against the scripted GameService: a join restores
// them, the checkpoint saves the changed players, and a leave and a disconnect send the final state
// with the release once the body went, as a shutdown does at once.
public sealed class RealtimeCharacterPersistenceTests
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task ASavedPlaceOfThisWorldAndCollisionIsWhereTheCharacterStartsWithItsVitals()
    {
        await using var harness = Create();
        var character = harness.AddCharacter("Returning Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 37, mana: 12)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();

        var client = await harness.JoinAsync(character);

        var start = client.JoinAccepted!.InitialPlayerState;
        Assert.Equal((3.5f, 0f, 2.25f, 90f), (start.PositionX, start.PositionY, start.PositionZ, start.YawDegrees));
        await harness.PollUntilAsync(() => OwnerStates(client).Any(), "the owner combat state");
        var vitals = OwnerStates(client)[^1];
        Assert.Equal((37, 100, 12, 100), (vitals.Health, vitals.MaximumHealth, vitals.Mana, vitals.MaximumMana));
    }

    [Fact]
    public async Task ASavedPlaceOfAnotherWorldOrCollisionStartsAtTheSpawnWithTheSavedVitals()
    {
        await using var harness = Create();
        var otherWorld = harness.AddCharacter("Other World Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 37, mana: 12) with { WorldId = "development-world-2" }
        };
        harness.GameService.AddCharacter(otherWorld);
        var otherCollision = harness.AddCharacter("Other Collision Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 64, mana: 0) with { CollisionRevision = "another-collision-revision" }
        };
        harness.GameService.AddCharacter(otherCollision);
        await harness.StartAsync();

        var otherWorldClient = await harness.JoinAsync(otherWorld);
        var otherCollisionClient = await harness.JoinAsync(otherCollision);

        foreach (var (client, health, mana) in new[] { (otherWorldClient, 37, 12), (otherCollisionClient, 64, 0) })
        {
            var start = client.JoinAccepted!.InitialPlayerState;
            Assert.Equal((0f, 0f, -1f, 0f), (start.PositionX, start.PositionY, start.PositionZ, start.YawDegrees));
            await harness.PollUntilAsync(() => OwnerStates(client).Any(), "the owner combat state");
            var vitals = OwnerStates(client)[^1];
            Assert.Equal((health, mana), (vitals.Health, vitals.Mana));
        }
    }

    [Fact]
    public async Task ACharacterWithoutASavedStateStartsAtTheSpawnWithFullVitals()
    {
        await using var harness = Create();
        var character = harness.AddCharacter("New Hero");
        await harness.StartAsync();

        var client = await harness.JoinAsync(character);

        var start = client.JoinAccepted!.InitialPlayerState;
        Assert.Equal((0f, 0f, -1f), (start.PositionX, start.PositionY, start.PositionZ));
        await harness.PollUntilAsync(() => OwnerStates(client).Any(), "the owner combat state");
        Assert.Equal((100, 100), (OwnerStates(client)[^1].Health, OwnerStates(client)[^1].Mana));
    }

    [Fact]
    public async Task ALeaveSendsTheFinalStateWithTheRelease()
    {
        await using var harness = Create(bodySeconds: 1);
        var character = harness.AddCharacter("Leaving Hero") with
        {
            SavedState = Saved(harness, -4f, 6.5f, yaw: 180f, health: 51, mana: 7)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendLeave(character.SimulationSessionId.ToString("D"));

        await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release after the body");
        var release = Assert.Single(harness.GameService.Releases);
        Assert.Equal(character.SimulationSessionId, release.SimulationSessionId);
        AssertState(release.Request.FinalState, -4f, 6.5f, 180f, 51, 7);
    }

    [Fact]
    public async Task ADisconnectSendsTheFinalStateWithTheRelease()
    {
        await using var harness = Create(bodySeconds: 1);
        var character = harness.AddCharacter("Vanishing Hero") with
        {
            SavedState = Saved(harness, 2f, 3f, yaw: 45f, health: 22, mana: 99)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Stop();

        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release");
        AssertState(harness.GameService.Releases[0].Request.FinalState, 2f, 3f, 45f, 22, 99);
        await harness.PollUntilAsync(() => harness.ReleaseService.FinalStates.Count == 0, "the release to forget the state");
    }

    // The player died and left before its respawn; a body whose death committed goes at once, and
    // the respawn would place it at the World spawn with full health and mana, so that is what is
    // saved.
    [Fact]
    public async Task ADeadPlayerIsSavedAtTheSpawnWithFullVitals()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 500, maximumDamage: 500),
            CombatRules = TestRules(respawnSeconds: 30f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Graveyard Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Graveyard Victim") with
        {
            SavedState = Saved(harness, 1.5f, -1f, yaw: 30f, health: 37, mana: 12)
        };
        harness.GameService.AddCharacter(victim);
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == victimEntityId),
            "the victim to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));
        await harness.PollUntilAsync(
            () => Events(victimClient).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == victimEntityId),
            "the victim to die");
        victimClient.SendLeave(victim.SimulationSessionId.ToString("D"));

        await harness.PollUntilAsync(
            () => harness.GameService.Releases.Any(release => release.SimulationSessionId == victim.SimulationSessionId),
            "the victim's release");
        Assert.Single(harness.GameService.PlayerDeaths);
        var release = harness.GameService.Releases.Single(value => value.SimulationSessionId == victim.SimulationSessionId);
        AssertState(release.Request.FinalState, 0f, -1f, 0f, 100, 100, y: 0f);
    }

    // A reconnect keeps the entity, so the character stays where it walked to.
    [Fact]
    public async Task AReconnectKeepsThePlaceOfTheEntity()
    {
        await using var harness = Create();
        var character = harness.AddCharacter("Reconnecting Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 37, mana: 12)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var first = await harness.JoinAsync(character);
        first.SendMoveTo(1, 8f, 2.25f);
        await harness.PollUntilAsync(
            () => first.OwnerSnapshots.Any(snapshot => snapshot.State.PositionX > 3.6f),
            "the step");
        var reconnect = character with { Ticket = "ticket-reconnecting-hero", SimulationSessionToken = "reconnected-token" };
        harness.GameService.AddCharacter(reconnect);

        var second = await harness.JoinAsync(reconnect, "Reconnecting Hero again");

        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.True(second.JoinAccepted.InitialPlayerState.PositionX > 3.6f);
        await harness.PollUntilAsync(() => OwnerStates(second).Any(), "the owner combat state");
        Assert.Equal((37, 12), (OwnerStates(second)[^1].Health, OwnerStates(second)[^1].Mana));
    }

    [Fact]
    public async Task TheCheckpointSendsOnlyTheChangedPlayersAtTheInterval()
    {
        await using var harness = Create(checkpoint: true);
        var unchanged = harness.AddCharacter("Resting Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 37, mana: 12)
        };
        harness.GameService.AddCharacter(unchanged);
        var unsaved = harness.AddCharacter("Unsaved Hero");
        await harness.StartAsync();
        var unchangedClient = await harness.JoinAsync(unchanged);
        await harness.JoinAsync(unsaved);

        // The resting hero stands where GameService saved it; the new one has no saved state.
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count >= 1, "the first checkpoint");
        var first = harness.GameService.Checkpoints[0];
        var saved = Assert.Single(first.States);
        Assert.Equal(RealtimeServerHarness.RuntimeId, first.RuntimeId);
        Assert.Equal(unsaved.SimulationSessionId, saved.SimulationSessionId);
        Assert.Equal((0f, 0f, -1f, 100, 100), (saved.PositionX, saved.PositionY, saved.PositionZ, saved.Health, saved.Mana));

        // Nothing changed, so nothing is sent.
        await harness.PollForAsync(CheckpointInterval * 3);
        Assert.Single(harness.GameService.Checkpoints);

        unchangedClient.SendMoveTo(1, 6f, 2.25f);
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count >= 2, "the checkpoint of the walk");
        var walked = Assert.Single(harness.GameService.Checkpoints[1].States);
        Assert.Equal(unchanged.SimulationSessionId, walked.SimulationSessionId);
        Assert.True(walked.PositionX > 3.5f);
        Assert.True(walked.Sequence > saved.Sequence);
    }

    [Fact]
    public async Task ACheckpointWaitsForTheCallInFlightAndSendsAFailedOneAgain()
    {
        await using var harness = Create(checkpoint: true);
        var character = harness.AddCharacter("Patient Hero");
        var gate = new TestGate();
        var calls = 0;
        harness.GameService.OnCheckpoint = async (request, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return ScriptedGameServiceHandler.Json(new SimulationCharacterCheckpointResponse(request.States.Count, 0));
            }

            await gate.WaitAsync();
            return ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable.");
        };
        await harness.StartAsync();
        await harness.JoinAsync(character);

        await harness.PollUntilAsync(() => gate.HasEntered, "the first checkpoint");
        await harness.PollForAsync(CheckpointInterval * 3);
        Assert.Single(harness.GameService.Checkpoints);

        gate.Release();
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count >= 2, "the checkpoint after the failure");
        var failed = Assert.Single(harness.GameService.Checkpoints[0].States);
        var again = Assert.Single(harness.GameService.Checkpoints[1].States);
        Assert.Equal(character.SimulationSessionId, again.SimulationSessionId);
        Assert.Equal((failed.PositionX, failed.Health), (again.PositionX, again.Health));
        Assert.True(again.Sequence > failed.Sequence);

        // Saved now, so the next checkpoints have nothing to send.
        await harness.PollForAsync(CheckpointInterval * 3);
        Assert.Equal(2, harness.GameService.Checkpoints.Count);
    }

    [Fact]
    public async Task ASyntheticBotHasNoSavedState()
    {
        await using var harness = Create(checkpoint: true, bodySeconds: 1);
        var bot = harness.AddCharacter("Active Bot 1", isSyntheticBot: true);
        await harness.StartAsync();
        var client = await harness.JoinAsync(bot);

        await harness.PollForAsync(CheckpointInterval * 3);
        client.SendLeave(bot.SimulationSessionId.ToString("D"));

        await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release after the body");
        Assert.Empty(harness.GameService.Checkpoints);
        Assert.Null(Assert.Single(harness.GameService.Releases).Request.FinalState);
    }

    // The realtime shutdown saves every remaining player in one checkpoint while the sessions
    // hold; going offline then ends the sessions, and their releases carry nothing more.
    [Fact]
    public async Task AShutdownSavesTheRemainingPlayersBeforeTheWorkerGoesOffline()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CombatRules = TestRules(),
            RegistrationLease = lease
        });
        var first = harness.AddCharacter("First Sleeper") with
        {
            SavedState = Saved(harness, 3f, 4f, yaw: 10f, health: 11, mana: 22)
        };
        harness.GameService.AddCharacter(first);
        var second = harness.AddCharacter("Second Sleeper") with
        {
            SavedState = Saved(harness, -3f, -4f, yaw: 20f, health: 33, mana: 44)
        };
        harness.GameService.AddCharacter(second);
        using var host = SimulationWorkerStopOrderTests.BuildHost(
            harness,
            lease,
            new ItemCatalogRuntimeDocument(),
            harness.Options.CombatRules!);
        using var timeout = RealtimeServerHarness.CreateTimeout();

        await host.StartAsync(timeout.Token);
        try
        {
            await harness.PollUntilAsync(() => lease.IsValid, "the worker to register");
            await harness.JoinAsync(first);
            await harness.JoinAsync(second);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.Equal(
            ["checkpoint", "offline", "release", "release"],
            harness.GameService.Calls
                .Select(path => path[(path.LastIndexOf('/') + 1)..])
                .Where(call => call is "checkpoint" or "offline" or "release"));
        var states = Assert.Single(harness.GameService.Checkpoints).States;
        AssertState(states.Single(state => state.SimulationSessionId == first.SimulationSessionId), 3f, 4f, 10f, 11, 22);
        AssertState(states.Single(state => state.SimulationSessionId == second.SimulationSessionId), -3f, -4f, 20f, 33, 44);
        Assert.All(harness.GameService.Releases, release => Assert.Null(release.Request.FinalState));
    }

    // When the last checkpoint fails, every release still carries its player's final state.
    [Fact]
    public async Task AFailedShutdownCheckpointLeavesTheFinalStatesToTheReleases()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CombatRules = TestRules(),
            RegistrationLease = lease
        });
        harness.GameService.OnCheckpoint = (_, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(HttpStatusCode.ServiceUnavailable, "database_unavailable", "PostgreSQL is unavailable."));
        var sleeper = harness.AddCharacter("Unlucky Sleeper") with
        {
            SavedState = Saved(harness, 3f, 4f, yaw: 10f, health: 11, mana: 22)
        };
        harness.GameService.AddCharacter(sleeper);
        using var host = SimulationWorkerStopOrderTests.BuildHost(
            harness,
            lease,
            new ItemCatalogRuntimeDocument(),
            harness.Options.CombatRules!);
        using var timeout = RealtimeServerHarness.CreateTimeout();

        await host.StartAsync(timeout.Token);
        try
        {
            await harness.PollUntilAsync(() => lease.IsValid, "the worker to register");
            await harness.JoinAsync(sleeper);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.Single(harness.GameService.Checkpoints);
        AssertState(Assert.Single(harness.GameService.Releases).Request.FinalState, 3f, 4f, 10f, 11, 22);
        Assert.Equal(0, harness.ReleaseService.FinalStates.Count);
    }

    private static RealtimeServerHarness Create(bool checkpoint = false, int? bodySeconds = null)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            // Without regeneration the vitals stay as restored.
            CombatRules = TestRules(),
            CharacterCheckpointInterval = checkpoint ? CheckpointInterval : null,
            LingeringBodySeconds = bodySeconds
        });
    }

    // A state that GameService saved on the ground of the harness World with its collision.
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
        int mana,
        float y = 0f)
    {
        Assert.NotNull(state);
        Assert.True(state.Sequence > 0);
        Assert.Equal((x, y, z, yaw, health, mana), (state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees, state.Health, state.Mana));
    }

    private static void AssertState(
        SimulationCharacterCheckpointEntry state,
        float x,
        float z,
        float yaw,
        int health,
        int mana)
    {
        Assert.True(state.Sequence > 0);
        Assert.Equal((x, 0f, z, yaw, health, mana), (state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees, state.Health, state.Mana));
    }
}
