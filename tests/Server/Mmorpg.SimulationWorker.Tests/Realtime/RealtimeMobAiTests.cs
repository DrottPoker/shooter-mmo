using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Mob brains over real UDP: detection, chase, bites, kills, the leash and the evade home,
// defensive brains, and a crowd that shares the planning budget. The player spawns at
// (0, 0, -1) on the 30 m test ground, which blocks no line of sight.
public sealed class RealtimeMobAiTests
{
    [Fact]
    public async Task AnAggressiveWolfChasesAndBitesAPlayerItDetects()
    {
        await using var harness = Create(WolvesAt([6f]));
        var player = harness.AddCharacter("Wolf Bait");
        await harness.StartAsync();
        var client = await harness.JoinAsync(player);
        var wolf = await WaitForWolfAsync(harness, client);
        var playerEntityId = client.JoinAccepted!.ControlledEntityId;

        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Hit
                && value.SourceEntityId == wolf.EntityId
                && value.TargetEntityId == playerEntityId),
            "the wolf's bite");

        var bite = Events(client).First(value => value.Kind == RealtimeCombatEventKind.AttackReleased
            && value.SourceEntityId == wolf.EntityId);
        Assert.Equal(playerEntityId, bite.TargetEntityId);
        Assert.Contains(States(client), value => value.EntityId == wolf.EntityId
            && value.TargetEntityId == playerEntityId
            && value.InCombat);
        await harness.PollUntilAsync(
            () => OwnerStates(client).Any(value => value.Health < value.MaximumHealth),
            "the player's lost health");
        // The wolf travelled in the movement snapshots and came to rest next to the player.
        Assert.Contains(
            client.Decode<RealtimeSimulationSnapshot>(
                    RealtimeMessageType.SimulationSnapshot,
                    RealtimeProtocol.TryDecodeSimulationSnapshot)
                .SelectMany(value => value.Entities),
            entity => entity.EntityId == wolf.EntityId);
        await harness.PollUntilAsync(
            () => WolfStates(client, wolf.EntityId).Any(value => PlanarDistance(value.PositionX, value.PositionZ, 0f, -1f) < 2f),
            "the wolf's rest pose next to the player");
        Assert.True(WolfStates(client, wolf.EntityId)[^1].StateRevision > wolf.StateRevision);
    }

    [Fact]
    public async Task AWolfThatKillsAPlayerIsNamedAsItsKiller()
    {
        await using var harness = Create(WolvesAt([3f], damage: 500));
        var player = harness.AddCharacter("Unlucky Hiker");
        await harness.StartAsync();
        var client = await harness.JoinAsync(player);
        var wolf = await WaitForWolfAsync(harness, client);

        await harness.PollUntilAsync(
            () => OwnerStates(client).Any(value => value.LifeState == RealtimeCombatLifeState.Dead),
            "the player to die");
        await harness.PollUntilAsync(
            () => harness.GameService.PlayerDeaths.Count > 0,
            "the death partition");

        var killer = Assert.Single(harness.GameService.PlayerDeaths).Killer;
        Assert.Equal("mob", killer!.Kind);
        Assert.Equal("mob.feral_wolf", killer.Id);
        Assert.Contains(Events(client), value => value.Kind == RealtimeCombatEventKind.Died
            && value.SourceEntityId == wolf.EntityId);
        // With its only target dead the wolf walks home.
        await harness.PollUntilAsync(
            () => States(client).Any(value => value.EntityId == wolf.EntityId
                && value.LifeState == RealtimeCombatLifeState.Evading),
            "the wolf to evade");
    }

    [Fact]
    public async Task AWolfPastItsLeashEvadesHomeInvulnerableAndHealsFully()
    {
        await using var harness = Create(
            WolvesAt([6f], leashRange: 3f),
            catalog: TestCatalog(minimumDamage: 1, maximumDamage: 1));
        var archer = harness.AddCharacter("Patient Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);
        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));

        await harness.PollUntilAsync(
            () => IndexOfState(client, wolf.EntityId, value => value.Health < value.MaximumHealth) >= 0,
            "an arrow to hit the wolf");
        await harness.PollUntilAsync(
            () => IndexOfState(client, wolf.EntityId, value => value.LifeState == RealtimeCombatLifeState.Evading) >= 0,
            "the wolf to evade");
        var evading = IndexOfState(client, wolf.EntityId, value => value.LifeState == RealtimeCombatLifeState.Evading);
        await harness.PollUntilAsync(
            () => IndexOfState(client, wolf.EntityId, value => value.LifeState == RealtimeCombatLifeState.Alive
                && value.Health == value.MaximumHealth, evading) >= 0,
            "the wolf to arrive home healed");

        var states = States(client).Where(value => value.EntityId == wolf.EntityId).ToArray();
        Assert.All(states.Skip(evading).TakeWhile(value => value.LifeState == RealtimeCombatLifeState.Evading),
            value => Assert.Equal(0ul, value.TargetEntityId));
        await harness.PollUntilAsync(
            () => WolfStates(client, wolf.EntityId).Any(value => PlanarDistance(value.PositionX, value.PositionZ, 6f, -1f) < 1f
                && value.StateRevision > wolf.StateRevision),
            "the wolf's rest pose at home");
    }

    [Fact]
    public async Task ADefensiveWolfFightsOnlyAfterItIsHit()
    {
        await using var harness = Create(
            WolvesAt([6f], aggression: WorldActorAggressionIds.Defensive),
            catalog: TestCatalog(minimumDamage: 1, maximumDamage: 1));
        var archer = harness.AddCharacter("Careful Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);
        var archerEntityId = client.JoinAccepted!.ControlledEntityId;

        // Long enough for the wolf to become active and think many times.
        await harness.PollForAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(Events(client), value => value.SourceEntityId == wolf.EntityId);
        Assert.DoesNotContain(States(client), value => value.EntityId == wolf.EntityId && value.TargetEntityId != 0);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));

        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Hit
                && value.SourceEntityId == wolf.EntityId
                && value.TargetEntityId == archerEntityId),
            "the wolf to bite back");
    }

    [Fact]
    public async Task ACrowdOfWolvesSharesThePlanningBudgetAndEveryWolfReachesThePlayer()
    {
        const int crowd = 60;
        var positions = Enumerable.Range(0, crowd)
            .Select(index => (X: 3f + (index % 6) * 0.6f, Z: -4f + (index / 6) * 0.6f))
            .ToArray();
        await using var harness = Create(
            WolvesAt(positions.Select(value => value.X).ToArray(), damage: 1, positionsZ: positions.Select(value => value.Z).ToArray()),
            rules: RulesWithHealth(100_000));
        var player = harness.AddCharacter("Crowd Magnet");
        await harness.StartAsync();
        var client = await harness.JoinAsync(player);
        var playerEntityId = client.JoinAccepted!.ControlledEntityId;

        await harness.PollUntilAsync(
            () => Events(client)
                .Where(value => value.Kind == RealtimeCombatEventKind.AttackReleased && value.TargetEntityId == playerEntityId)
                .Select(value => value.SourceEntityId)
                .Distinct()
                .Count() == crowd,
            "every wolf to bite");

        Assert.Equal(crowd, WolfSpawns(client).Count);
    }

    private static RealtimeServerHarness Create(
        WorldActorRuntimeDocument document,
        ItemCatalogRuntimeDocument? catalog = null,
        CombatRules? rules = null)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            WorldActorDocument = document,
            ItemCatalog = catalog ?? TestCatalog(),
            CombatRules = rules ?? TestRules(respawnSeconds: 30f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
    }

    // Wolves at the given positions without patrols; the brain and the bite can be tuned.
    private static WorldActorRuntimeDocument WolvesAt(
        float[] positionsX,
        int damage = 4,
        float leashRange = 30f,
        string aggression = WorldActorAggressionIds.Aggressive,
        float[]? positionsZ = null)
    {
        var document = WolfNextToTheSpawn(respawnSeconds: 30f, x: positionsX[0]);
        var template = document.SpawnInstances.Single(spawn => spawn.ActorId == "mob.feral_wolf");
        template.Z = positionsZ?[0] ?? -1f;
        var wolves = positionsX
            .Select((x, index) => index == 0
                ? template
                : new WorldActorSpawnInstanceDefinition
                {
                    Id = template.Id + ".crowd." + index,
                    SpawnDefinitionId = template.SpawnDefinitionId,
                    ActorId = template.ActorId,
                    X = x,
                    Y = template.Y,
                    Z = positionsZ?[index] ?? -1f,
                    YawDegrees = template.YawDegrees,
                    PatrolPathId = string.Empty,
                    StructuralFingerprint = template.StructuralFingerprint + "." + index
                })
            .ToArray();
        document.SpawnInstances = document.SpawnInstances
            .Where(spawn => spawn.ActorId != "mob.feral_wolf")
            .Concat(wolves)
            .ToArray();
        var definition = document.Actors.Single(actor => actor.Id == "mob.feral_wolf");
        var combat = document.CombatProfiles.Single(profile => profile.Id == definition.CombatProfileId);
        combat.MinimumDamage = damage;
        combat.MaximumDamage = damage;
        var brain = document.BrainProfiles.Single(profile => profile.Id == definition.BrainProfileId);
        brain.LeashRange = leashRange;
        brain.Aggression = aggression;
        return document;
    }

    private static CombatRules RulesWithHealth(int maximumHealth)
    {
        return CombatRules.FromDocument(new CombatRulesDocument
        {
            formatVersion = CombatRulesFormat.Version,
            player = new PlayerCombatRulesDocument
            {
                maximumHealth = maximumHealth,
                respawnDelaySeconds = 30f,
                combatTimeoutSeconds = 10f,
                regenerationPercentPerSecond = 0,
                equipmentLockoutSeconds = 0.5f
            },
            ammunition = new AmmunitionRulesDocument { batchIntervalSeconds = 5f },
            ruleSets = [new ShardRuleSetDocument { id = "test", playerVersusPlayer = true }]
        });
    }

    private static IReadOnlyList<RealtimeWorldActorState> WolfStates(RealtimeRecordingClient client, ulong entityId)
    {
        return client.Decode<RealtimeWorldActorState>(RealtimeMessageType.WorldActorState, RealtimeProtocol.TryDecodeWorldActorState)
            .Where(value => value.EntityId == entityId)
            .ToArray();
    }

    private static int IndexOfState(
        RealtimeRecordingClient client,
        ulong entityId,
        Func<RealtimeCombatantState, bool> match,
        int after = -1)
    {
        var states = States(client).Where(value => value.EntityId == entityId).ToArray();
        for (var index = after + 1; index < states.Length; index++)
        {
            if (match(states[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static float PlanarDistance(float x, float z, float otherX, float otherZ)
    {
        return MathF.Sqrt(((x - otherX) * (x - otherX)) + ((z - otherZ) * (z - otherZ)));
    }
}
