using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Combat;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Target-based combat over real UDP against the scripted GameService: attacks, arrow
// batches, Mob and player deaths, and respawns.
public sealed class RealtimeCombatTests
{
    private const string Bow = "test.bow";
    private const string Arrow = "test.arrow";

    [Fact]
    public async Task AnArcherKillsAWolfThatLeavesACorpseAndRespawns()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableMobCorpses = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: 1f),
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Wolf Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));

        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
        var result = Assert.Single(client.Decode<RealtimeCombatIntentResult>(
            RealtimeMessageType.CombatIntentResult,
            RealtimeProtocol.TryDecodeCombatIntentResult));
        Assert.True(result.Succeeded);
        Assert.Equal(wolf.EntityId, result.TargetEntityId);
        var released = Events(client).First(value => value.Kind == RealtimeCombatEventKind.AttackReleased);
        var hit = Events(client).First(value => value.Kind == RealtimeCombatEventKind.Hit);
        Assert.Equal(client.JoinAccepted!.ControlledEntityId, released.SourceEntityId);
        Assert.True(released.ImpactTick > 0);
        Assert.Equal(wolf.EntityId, hit.TargetEntityId);
        Assert.Equal(0, hit.HealthAfter);

        await harness.PollUntilAsync(
            () => client.Decode<RealtimeWorldActorDespawn>(RealtimeMessageType.WorldActorDespawn, RealtimeProtocol.TryDecodeWorldActorDespawn)
                .Any(value => value.EntityId == wolf.EntityId && value.Reason == "died"),
            "the wolf to despawn");
        Assert.Contains(harness.LiveMobCorpseStore!.ListActive(), corpse => corpse.SourceActorRuntimeId == wolf.RuntimeActorId);

        await harness.PollUntilAsync(
            () => WolfSpawns(client).Any(value => value.EntityId != wolf.EntityId && value.SpawnDefinitionId == wolf.SpawnDefinitionId),
            "the wolf to respawn");
        var respawned = WolfSpawns(client).Last(value => value.SpawnDefinitionId == wolf.SpawnDefinitionId);
        Assert.NotEqual(wolf.RuntimeActorId, respawned.RuntimeActorId);
        await harness.PollUntilAsync(
            () => States(client).Any(value => value.EntityId == respawned.EntityId && value.Health == value.MaximumHealth),
            "the respawned wolf's state");
    }

    [Fact]
    public async Task AttacksWaitForRangeAndReportWhy()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: 1f, x: 13.5f),
            ItemCatalog = TestCatalog(range: 3f),
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Far Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));

        await harness.PollUntilAsync(
            () => OwnerStates(client).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.OutOfRange),
            "the out of range status");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(600));
        Assert.DoesNotContain(Events(client), value => value.Kind == RealtimeCombatEventKind.AttackReleased);
        Assert.Equal(wolf.EntityId, OwnerStates(client)[^1].TargetEntityId);

        client.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        await harness.PollUntilAsync(
            () => OwnerStates(client).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.Idle && value.TargetEntityId == 0),
            "the cleared target");
    }

    [Fact]
    public async Task AttackingAnotherCharacterNeedsTheShardRule()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules(),
            PlayerVersusPlayer = false
        });
        var archer = harness.AddCharacter("Peaceful Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Peaceful Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.EntitySpawn) && States(archerClient).Any(value => value.EntityId == targetClient.JoinAccepted!.ControlledEntityId),
            "the target to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetClient.JoinAccepted!.ControlledEntityId));
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), archerClient.JoinAccepted!.ControlledEntityId));

        await harness.PollUntilAsync(
            () => archerClient.Decode<RealtimeCombatIntentResult>(RealtimeMessageType.CombatIntentResult, RealtimeProtocol.TryDecodeCombatIntentResult).Count == 2,
            "both intent results");
        var results = archerClient.Decode<RealtimeCombatIntentResult>(
            RealtimeMessageType.CombatIntentResult,
            RealtimeProtocol.TryDecodeCombatIntentResult);
        Assert.False(archerClient.JoinAccepted.ShardRules.PlayerVersusPlayer);
        Assert.Equal(RealtimeCombatErrorCodes.PlayerVersusPlayerDisabled, results[0].Error!.Code);
        Assert.Equal(RealtimeCombatErrorCodes.TargetSelf, results[1].Error!.Code);
        Assert.All(results, value => Assert.Equal(0ul, value.TargetEntityId));
    }

    [Fact]
    public async Task ASyntheticBotIsNoCombatTarget()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules(),
            PlayerVersusPlayer = true
        });
        var archer = harness.AddCharacter("Bot Hunter") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var bot = harness.AddCharacter("Synthetic Bot") with { IsSyntheticBot = true };
        harness.GameService.AddCharacter(bot);
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var botClient = await harness.JoinAsync(bot);
        var botEntityId = botClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => archerClient.Spawns.Any(value => value.EntityId == botEntityId),
            "the bot to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), botEntityId));

        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.CombatIntentResult),
            "the intent result");
        var result = Assert.Single(archerClient.Decode<RealtimeCombatIntentResult>(
            RealtimeMessageType.CombatIntentResult,
            RealtimeProtocol.TryDecodeCombatIntentResult));
        Assert.Equal(RealtimeCombatErrorCodes.TargetNotAttackable, result.Error!.Code);
        Assert.DoesNotContain(States(archerClient), value => value.EntityId == botEntityId);
        Assert.False(botClient.Has(RealtimeMessageType.OwnerCombatState));
    }

    [Fact]
    public async Task SpentArrowsCommitInBatchesAndTheOwnerSeesTheArrowCount()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Batch Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Batch Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));

        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.OwnerItemQuantityChanged),
            "a committed arrow batch");
        var batch = harness.GameService.AmmunitionConsumptions[0];
        Assert.Equal(archer.CharacterId, batch.CharacterId);
        Assert.Equal(ArcherEquipment(50)[1].ItemInstanceId, batch.ItemInstanceId);
        Assert.InRange(batch.Quantity, 1, 3);
        var change = archerClient.Decode<RealtimeOwnerItemQuantityChanged>(
            RealtimeMessageType.OwnerItemQuantityChanged,
            RealtimeProtocol.TryDecodeOwnerItemQuantityChanged)[0];
        Assert.Equal(RealtimeItemQuantityChangeReason.AmmunitionConsumed, change.Reason);
        Assert.Equal(batch.ItemInstanceId, change.ItemInstanceId);
        Assert.Equal(50 - batch.Quantity, change.Quantity);
        Assert.Equal(batch.ExpectedCharacterRevision, change.PreviousItemStateRevision);
        Assert.Contains(OwnerStates(archerClient), value => value.AmmunitionQuantity < 50);
        Assert.Contains(
            Events(targetClient),
            value => value.Kind == RealtimeCombatEventKind.Hit && value.TargetEntityId == targetEntityId);
    }

    [Fact]
    public async Task AKilledCharacterCommitsItsDeathOnceAndRespawnsAtTheSpawn()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 500, maximumDamage: 500),
            CombatRules = TestRules(respawnSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Duel Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Duel Victim");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == victimEntityId),
            "the victim to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));

        await harness.PollUntilAsync(
            () => OwnerStates(victimClient).Any(value => value.LifeState == RealtimeCombatLifeState.Dead),
            "the victim to die");
        await harness.PollUntilAsync(
            () => OwnerStates(victimClient).Any(value => value.LifeState == RealtimeCombatLifeState.Alive && value.LifeSequence == 2),
            "the victim to respawn");
        var death = Assert.Single(harness.GameService.PlayerDeaths);
        Assert.Equal(victim.CharacterId, death.CharacterId);
        Assert.Equal("character", death.Killer!.Kind);
        Assert.Equal(archer.CharacterId.ToString("D"), death.Killer.Id);
        Assert.Null(death.AmmunitionConsumption);
        var dead = OwnerStates(victimClient).First(value => value.LifeState == RealtimeCombatLifeState.Dead);
        Assert.Equal(0, dead.Health);
        Assert.True(dead.RespawnTick > 0);
        var alive = OwnerStates(victimClient).Last();
        Assert.Equal(alive.MaximumHealth, alive.Health);
        await harness.PollUntilAsync(
            () => archerClient.Decode<RealtimeEntityDespawn>(RealtimeMessageType.EntityDespawn, RealtimeProtocol.TryDecodeEntityDespawn)
                .Any(value => value.EntityId == victimEntityId && value.Reason == "respawned"),
            "the respawn for observers");
        Assert.Contains(
            OwnerStates(archerClient),
            value => value.TargetEntityId == 0 && value.AttackStatus == RealtimeCombatAttackStatus.Idle);

        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Single(harness.GameService.PlayerDeaths);
    }

    [Fact]
    public async Task ALeaveCommitsUncommittedShotsBeforeTheSessionIsReleased()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Leaving Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Leaving Target");
        var releasesAtConsumption = -1;
        harness.GameService.OnAmmunitionConsumption = (_, request, _) =>
        {
            releasesAtConsumption = harness.GameService.ReleaseCount;
            return Task.FromResult(harness.GameService.DefaultAmmunitionConsumption(request));
        };
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => Events(archerClient).Any(value => value.Kind == RealtimeCombatEventKind.AttackReleased),
            "a shot");
        Assert.Empty(harness.GameService.AmmunitionConsumptions);

        archerClient.SendLeave(archerClient.JoinAccepted!.SimulationSessionId);

        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.LeaveAccepted),
            "the leave");
        var batch = Assert.Single(harness.GameService.AmmunitionConsumptions);
        Assert.True(batch.Quantity >= 1);
        Assert.Equal(0, releasesAtConsumption);
    }

    [Fact]
    public async Task ADisconnectCommitsUncommittedShotsBeforeTheSessionIsReleased()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Vanishing Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Vanishing Target");
        var releasesAtConsumption = -1;
        harness.GameService.OnAmmunitionConsumption = (_, request, _) =>
        {
            releasesAtConsumption = harness.GameService.ReleaseCount;
            return Task.FromResult(harness.GameService.DefaultAmmunitionConsumption(request));
        };
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => Events(archerClient).Any(value => value.Kind == RealtimeCombatEventKind.AttackReleased),
            "a shot");

        archerClient.Stop();

        await harness.PollUntilAsync(
            () => harness.GameService.ReleaseCount >= 1,
            "the detached release");
        Assert.Single(harness.GameService.AmmunitionConsumptions);
        Assert.Equal(0, releasesAtConsumption);
    }

    [Fact]
    public async Task EquippingABowHoldsAttacksForTheLockout()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Unarmed Archer") with { Equipment = [ArcherEquipment(50)[1]] };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Lockout Target");
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.Json(
            new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [new SimulationItemCharacterRevision(request.CharacterId, 1, 0, 200)],
                [],
                [new SimulationItemInstanceRevision(BowItemId, 2)],
                [],
                null,
                CharacterStateTestData.Create(request.CharacterId, 1, equipment: ArcherEquipment(50)))));
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.WeaponMissing),
            "the missing weapon");

        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateEquip(Guid.NewGuid(), 0, BowItemId, 1, "main_hand"));

        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.EquipmentLockout && value.LockoutEndTick > 0),
            "the equipment lockout");
        await harness.PollUntilAsync(
            () => Events(archerClient).Any(value => value.Kind == RealtimeCombatEventKind.AttackReleased),
            "the first shot after the lockout");
        var lockout = OwnerStates(archerClient).First(value => value.AttackStatus == RealtimeCombatAttackStatus.EquipmentLockout);
        var batch = archerClient.DecodeWithPackets<RealtimeCombatEventBatch>(
                RealtimeMessageType.CombatEventBatch,
                RealtimeProtocol.TryDecodeCombatEventBatch)
            .First(value => value.Value.Events.Any(item => item.Kind == RealtimeCombatEventKind.AttackReleased));
        Assert.True(batch.Value.ServerTick >= lockout.LockoutEndTick);
    }

    [Fact]
    public async Task ACharacterThatJoinedWithNothingEquippedGetsTheLockoutForItsFirstWeapon()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules()
        });
        var archer = harness.AddCharacter("Empty Handed Archer");
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.Json(
            new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [new SimulationItemCharacterRevision(request.CharacterId, 1, 0, 200)],
                [],
                [new SimulationItemInstanceRevision(BowItemId, 2)],
                [],
                null,
                CharacterStateTestData.Create(request.CharacterId, 1, equipment: [ArcherEquipment(50)[0]]))));
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Count > 0,
            "the first owner state");

        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateEquip(Guid.NewGuid(), 0, BowItemId, 1, "main_hand"));

        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.LockoutEndTick > 0),
            "the equipment lockout");
        Assert.Equal(0u, OwnerStates(archerClient)[0].LockoutEndTick);
    }

    [Fact]
    public async Task ArrowsAddedByAnItemOperationReachTheOwnerWithoutALockout()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules()
        });
        var archer = harness.AddCharacter("Restocking Archer") with { Equipment = ArcherEquipment(10) };
        harness.GameService.AddCharacter(archer);
        var carriedArrows = Guid.NewGuid();
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(ScriptedGameServiceHandler.Json(
            new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [new SimulationItemCharacterRevision(request.CharacterId, 1, 0, 200)],
                [],
                [new SimulationItemInstanceRevision(ArrowItemId, 2)],
                [],
                null,
                CharacterStateTestData.Create(
                    request.CharacterId,
                    1,
                    equipment:
                    [
                        ArcherEquipment(10)[0],
                        new SimulationEquippedItem("ammunition", ArrowItemId, Arrow, 30, 2)
                    ]))));
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.AmmunitionQuantity == 10),
            "the joined arrow count");

        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateEquip(Guid.NewGuid(), 0, carriedArrows, 1, "ammunition"));

        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.AmmunitionQuantity == 30),
            "the restocked arrow count");
        var restocked = OwnerStates(archerClient).First(value => value.AmmunitionQuantity == 30);
        Assert.Equal(ArrowItemId, restocked.AmmunitionItemInstanceId);
        Assert.Equal(0u, restocked.LockoutEndTick);
    }

    [Fact]
    public async Task ADeadCharacterStartsNoItemOperation()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 500, maximumDamage: 500),
            CombatRules = TestRules(respawnSeconds: 30f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Fatal Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Fallen Looter");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == victimEntityId),
            "the victim to appear");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));
        await harness.PollUntilAsync(
            () => OwnerStates(victimClient).Any(value => value.LifeState == RealtimeCombatLifeState.Dead),
            "the victim to die");

        victimClient.SendItemIntent(RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 1, Guid.NewGuid(), 1));
        victimClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));

        await harness.PollUntilAsync(
            () => victimClient.Has(RealtimeMessageType.ItemOperationResult)
                && victimClient.Has(RealtimeMessageType.CombatIntentResult),
            "both rejections");
        var itemResult = Assert.Single(victimClient.Decode<RealtimeItemOperationResult>(
            RealtimeMessageType.ItemOperationResult,
            RealtimeProtocol.TryDecodeItemOperationResult));
        Assert.Equal(RealtimeCombatErrorCodes.CharacterDead, itemResult.Error!.Code);
        var combatResult = Assert.Single(victimClient.Decode<RealtimeCombatIntentResult>(
            RealtimeMessageType.CombatIntentResult,
            RealtimeProtocol.TryDecodeCombatIntentResult));
        Assert.Equal(RealtimeCombatErrorCodes.CharacterDead, combatResult.Error!.Code);
        Assert.Equal(0, harness.GameService.ItemOperationCount);
    }

    [Fact]
    public async Task AnIntentOnARevisionOnlyArrowBatchesAdvancedUsesTheNewestRevision()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Rebasing Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Rebasing Target");
        var forwarded = new List<long>();
        harness.GameService.OnItemOperation = (_, request, _) =>
        {
            lock (forwarded)
            {
                forwarded.Add(request.ExpectedCharacterRevision);
            }

            return Task.FromResult(ScriptedGameServiceHandler.Problem(
                HttpStatusCode.Conflict,
                "item_state_conflict",
                "Test conflict."));
        };
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.OwnerItemQuantityChanged),
            "a committed arrow batch");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(700));
        var joinedRevision = archerClient.JoinAccepted!.CarryState.ItemStateRevision;
        var newest = archerClient.Decode<RealtimeOwnerItemQuantityChanged>(
                RealtimeMessageType.OwnerItemQuantityChanged,
                RealtimeProtocol.TryDecodeOwnerItemQuantityChanged)[^1].CarryState.ItemStateRevision;

        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), joinedRevision, Guid.NewGuid(), 1));

        await harness.PollUntilAsync(
            () => archerClient.Has(RealtimeMessageType.ItemOperationResult),
            "the forwarded intent");
        lock (forwarded)
        {
            Assert.Equal(newest, Assert.Single(forwarded));
        }

        Assert.True(newest > joinedRevision);
    }

    [Fact]
    public async Task TheLastArrowEndsTheVolley()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Thrifty Archer") with { Equipment = ArcherEquipment(1) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Thrifty Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));

        await harness.PollUntilAsync(
            () => OwnerStates(archerClient).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.AmmunitionMissing),
            "the empty quiver");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(700));
        Assert.Single(Events(archerClient), value => value.Kind == RealtimeCombatEventKind.AttackReleased);
        Assert.Equal(0, OwnerStates(archerClient)[^1].AmmunitionQuantity);
    }

    private static async Task<RealtimeWorldActorSpawn> WaitForWolfAsync(
        RealtimeServerHarness harness,
        RealtimeRecordingClient client)
    {
        await harness.PollUntilAsync(
            () => WolfSpawns(client).Count > 0
                && States(client).Any(value => value.EntityId == WolfSpawns(client)[0].EntityId),
            "the wolf and its combat state");
        return WolfSpawns(client)[0];
    }

    private static IReadOnlyList<RealtimeWorldActorSpawn> WolfSpawns(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeWorldActorSpawn>(RealtimeMessageType.WorldActorSpawn, RealtimeProtocol.TryDecodeWorldActorSpawn)
            .Where(value => value.ActorDefinitionId == "mob.feral_wolf")
            .ToArray();
    }

    private static IReadOnlyList<RealtimeCombatantState> States(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCombatantState>(RealtimeMessageType.CombatantState, RealtimeProtocol.TryDecodeCombatantState);
    }

    private static IReadOnlyList<RealtimeOwnerCombatState> OwnerStates(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeOwnerCombatState>(RealtimeMessageType.OwnerCombatState, RealtimeProtocol.TryDecodeOwnerCombatState);
    }

    private static IReadOnlyList<RealtimeCombatEvent> Events(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCombatEventBatch>(RealtimeMessageType.CombatEventBatch, RealtimeProtocol.TryDecodeCombatEventBatch)
            .SelectMany(value => value.Events)
            .ToArray();
    }

    private static readonly Guid BowItemId = Guid.Parse("b0b0b0b0-0000-4000-8000-000000000001");
    private static readonly Guid ArrowItemId = Guid.Parse("a0a0a0a0-0000-4000-8000-000000000002");

    private static IReadOnlyList<SimulationEquippedItem> ArcherEquipment(int arrows)
    {
        return
        [
            new SimulationEquippedItem("main_hand", BowItemId, Bow, 1, 1),
            new SimulationEquippedItem("ammunition", ArrowItemId, Arrow, arrows, 1)
        ];
    }

    // The fastest bow content allows: a shot every 0.5 s.
    internal static ItemCatalogRuntimeDocument TestCatalog(
        int minimumDamage = 10,
        int maximumDamage = 18,
        float range = 10f)
    {
        return new ItemCatalogRuntimeDocument
        {
            WeaponFamilies =
            [
                new ItemWeaponFamilyEntry
                {
                    Id = "bow",
                    CombatStyle = CombatStyleIds.Ranged,
                    AmmunitionFamily = "arrow",
                    ProjectileSpeed = 30f
                }
            ],
            Definitions =
            [
                new ItemDefinition
                {
                    Id = Bow,
                    Category = ItemCategoryIds.Weapon,
                    Weapon = new ItemWeaponEntry
                    {
                        WeaponFamily = "bow",
                        DamageType = DamageTypeIds.Normal,
                        MinimumDamage = minimumDamage,
                        MaximumDamage = maximumDamage,
                        AttackIntervalSeconds = 0.5f,
                        Range = range
                    }
                },
                new ItemDefinition
                {
                    Id = Arrow,
                    Category = ItemCategoryIds.Ammunition,
                    Ammunition = new ItemAmmunitionEntry { AmmunitionFamily = "arrow", DamageBonusPercent = 0 }
                }
            ]
        };
    }

    internal static CombatRules TestRules(float respawnSeconds = 0.5f, float batchIntervalSeconds = 5f)
    {
        return CombatRules.FromDocument(new CombatRulesDocument
        {
            formatVersion = CombatRulesFormat.Version,
            player = new PlayerCombatRulesDocument
            {
                maximumHealth = 100,
                respawnDelaySeconds = respawnSeconds,
                combatTimeoutSeconds = 10f,
                regenerationPercentPerSecond = 0,
                equipmentLockoutSeconds = 0.5f
            },
            ammunition = new AmmunitionRulesDocument { batchIntervalSeconds = batchIntervalSeconds },
            ruleSets = [new ShardRuleSetDocument { id = "test", playerVersusPlayer = true }]
        });
    }

    // One wolf a few metres from the World spawn, with a short respawn.
    private static WorldActorRuntimeDocument WolfNextToTheSpawn(float respawnSeconds, float x = 4f)
    {
        var document = WorldActorTestData.Compile();
        var wolf = document.SpawnInstances.First(spawn => spawn.ActorId == "mob.feral_wolf");
        document.SpawnInstances = document.SpawnInstances
            .Where(spawn => spawn.ActorId != "mob.feral_wolf" || ReferenceEquals(spawn, wolf))
            .ToArray();
        wolf.X = x;
        wolf.Z = -1f;
        wolf.PatrolPathId = string.Empty;
        foreach (var profile in document.RespawnProfiles)
        {
            profile.DelaySeconds = respawnSeconds;
        }

        return document;
    }

    private sealed class LowestRoll : ICombatRandom
    {
        public int NextInclusive(int minimum, int maximum)
        {
            return minimum;
        }
    }
}
