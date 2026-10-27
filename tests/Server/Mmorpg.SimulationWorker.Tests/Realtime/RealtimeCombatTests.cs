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
// batches, magic and mana, regeneration, Mob and player deaths, and respawns.
public sealed class RealtimeCombatTests
{
    private const string Bow = "test.bow";
    private const string Arrow = "test.arrow";
    private const string Staff = "test.staff";

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

    // Without a corpse the Mob leaves only after its viewers saw it die.
    [Fact]
    public async Task AWolfWithoutACorpseDespawnsAfterItsDeathReachedTheViewers()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: 30f),
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Tidy Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));

        await harness.PollUntilAsync(
            () => client.Decode<RealtimeWorldActorDespawn>(RealtimeMessageType.WorldActorDespawn, RealtimeProtocol.TryDecodeWorldActorDespawn)
                .Any(value => value.EntityId == wolf.EntityId),
            "the wolf to despawn");
        var despawn = client.DecodeWithPackets<RealtimeWorldActorDespawn>(
                RealtimeMessageType.WorldActorDespawn,
                RealtimeProtocol.TryDecodeWorldActorDespawn)
            .Single(value => value.Value.EntityId == wolf.EntityId);
        var death = client.DecodeWithPackets<RealtimeCombatEventBatch>(
                RealtimeMessageType.CombatEventBatch,
                RealtimeProtocol.TryDecodeCombatEventBatch)
            .Single(value => value.Value.Events.Any(item => item.Kind == RealtimeCombatEventKind.Died && item.TargetEntityId == wolf.EntityId));
        var deadState = client.DecodeWithPackets<RealtimeCombatantState>(
                RealtimeMessageType.CombatantState,
                RealtimeProtocol.TryDecodeCombatantState)
            .Single(value => value.Value.EntityId == wolf.EntityId && value.Value.LifeState == RealtimeCombatLifeState.Dead);
        Assert.Equal("died", despawn.Value.Reason);
        Assert.True(death.Packet.Index < despawn.Packet.Index);
        Assert.True(deadState.Packet.Index < despawn.Packet.Index);
    }

    // A lethal hit ends the target's life at once, so a second arrow of the same volley
    // neither hits nor takes the kill.
    [Fact]
    public async Task OnlyTheLethalArrowHitsAndTakesTheKill()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 500, maximumDamage: 500),
            CombatRules = TestRules(respawnSeconds: 30f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var first = harness.AddCharacter("First Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(first);
        var second = harness.AddCharacter("Second Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(second);
        var victim = harness.AddCharacter("Volley Victim");
        await harness.StartAsync();
        var firstClient = await harness.JoinAsync(first);
        var secondClient = await harness.JoinAsync(second);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(firstClient).Any(value => value.EntityId == victimEntityId)
                && States(secondClient).Any(value => value.EntityId == victimEntityId),
            "the victim to appear");

        firstClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));
        secondClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));

        await harness.PollUntilAsync(
            () => Events(victimClient).Any(value => value.Kind == RealtimeCombatEventKind.Died),
            "the victim to die");
        await harness.PollUntilAsync(
            () => harness.GameService.PlayerDeaths.Count > 0,
            "the death partition");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));
        var hit = Assert.Single(Events(victimClient), value => value.Kind == RealtimeCombatEventKind.Hit);
        var died = Assert.Single(Events(victimClient), value => value.Kind == RealtimeCombatEventKind.Died);
        Assert.Equal(hit.SourceEntityId, died.SourceEntityId);
        var killer = hit.SourceEntityId == firstClient.JoinAccepted!.ControlledEntityId ? first : second;
        Assert.Equal(killer.CharacterId.ToString("D"), Assert.Single(harness.GameService.PlayerDeaths).Killer!.Id);
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

    // A batch that finished while the worker stopped is settled, so the shots fired after
    // it still commit on top of its revision.
    [Fact]
    public async Task AStoppingWorkerCommitsTheShotsBehindABatchThatJustFinished()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Last Call Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Last Call Target");
        var firstBatchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        harness.GameService.OnAmmunitionConsumption = async (_, request, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await firstBatchGate.Task;
            }

            return harness.GameService.DefaultAmmunitionConsumption(request);
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
            () => harness.GameService.AmmunitionConsumptions.Count == 1,
            "the first batch to be in flight");
        var firstBatch = harness.GameService.AmmunitionConsumptions[0];
        await harness.PollUntilAsync(
            () => Events(archerClient).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased) >= firstBatch.Quantity + 2,
            "more shots behind the batch");

        var stopping = harness.StopAsync();
        firstBatchGate.SetResult();
        await stopping;

        Assert.Equal(2, harness.GameService.AmmunitionConsumptions.Count);
        var lastBatch = harness.GameService.AmmunitionConsumptions[1];
        Assert.Equal(firstBatch.ExpectedCharacterRevision + 1, lastBatch.ExpectedCharacterRevision);
        Assert.Equal(firstBatch.ExpectedItemRevision + 1, lastBatch.ExpectedItemRevision);
        Assert.True(lastBatch.Quantity >= 2);
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

    // Every bolt spends its mana when it leaves the staff; a caster without enough keeps its target
    // and waits, while the owner sees its mana fall.
    [Fact]
    public async Task ACasterSpendsManaOnEveryBoltUntilItRunsShort()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1, manaCost: 5),
            CombatRules = TestRules(maximumMana: 12),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var caster = harness.AddCharacter("Thrifty Caster") with { Equipment = CasterEquipment() };
        harness.GameService.AddCharacter(caster);
        var target = harness.AddCharacter("Caster Target");
        await harness.StartAsync();
        var casterClient = await harness.JoinAsync(caster);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(casterClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        Assert.Equal((12, 12), (OwnerStates(casterClient)[^1].Mana, OwnerStates(casterClient)[^1].MaximumMana));

        casterClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));

        await harness.PollUntilAsync(
            () => OwnerStates(casterClient).Any(value => value.AttackStatus == RealtimeCombatAttackStatus.NotEnoughMana),
            "the caster to run short of mana");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(700));
        var released = Events(casterClient).Where(value => value.Kind == RealtimeCombatEventKind.AttackReleased).ToArray();
        Assert.Equal(2, released.Length);
        Assert.All(released, value => Assert.Equal(RealtimeCombatAttackStyle.Magic, value.AttackStyle));
        Assert.All(released, value => Assert.True(value.ImpactTick > 0));
        var owner = OwnerStates(casterClient)[^1];
        Assert.Equal((2, 12), (owner.Mana, owner.MaximumMana));
        Assert.Equal(targetEntityId, owner.TargetEntityId);
        Assert.Equal(RealtimeCombatAttackStatus.NotEnoughMana, owner.AttackStatus);
        // Only the owner learns its mana; the target sees its own full bar.
        Assert.Equal((12, 12), (OwnerStates(targetClient)[^1].Mana, OwnerStates(targetClient)[^1].MaximumMana));
    }

    // Out of combat a caster regains mana and casts again by itself, and its target regains
    // health; neither regenerates while it fights.
    [Fact]
    public async Task ManaAndHealthRegenerateOnlyOutOfCombat()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 3, maximumDamage: 3, manaCost: 5),
            CombatRules = TestRules(maximumMana: 5, combatTimeoutSeconds: 0.5f, healthRegenerationPerSecond: 1, manaRegenerationPerSecond: 5),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var caster = harness.AddCharacter("Patient Caster") with { Equipment = CasterEquipment() };
        harness.GameService.AddCharacter(caster);
        var target = harness.AddCharacter("Resting Target");
        await harness.StartAsync();
        var casterClient = await harness.JoinAsync(caster);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(casterClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");

        casterClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));

        await harness.PollUntilAsync(
            () => Events(casterClient).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased) >= 2,
            "a second bolt after the mana came back");
        casterClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        var states = OwnerStates(casterClient);
        var empty = states.ToList().FindIndex(value => value.Mana == 0);
        Assert.True(empty >= 0);
        // Between the two bolts the caster waited with no mana until it left combat.
        var refilled = states.Skip(empty).First(value => value.Mana == 5);
        Assert.False(refilled.InCombat);
        Assert.DoesNotContain(states.Skip(empty).TakeWhile(value => value.InCombat), value => value.Mana > 0);

        await harness.PollUntilAsync(
            () => States(targetClient).Where(value => value.EntityId == targetEntityId).Select(value => value.Health)
                .SkipWhile(health => health == 100).Any(health => health > 94),
            "the target to regain health after the last bolt");
        var lowest = States(targetClient).Where(value => value.EntityId == targetEntityId).Min(value => value.Health);
        Assert.True(lowest <= 97);
    }

    internal static async Task<RealtimeWorldActorSpawn> WaitForWolfAsync(
        RealtimeServerHarness harness,
        RealtimeRecordingClient client)
    {
        await harness.PollUntilAsync(
            () => WolfSpawns(client).Count > 0
                && States(client).Any(value => value.EntityId == WolfSpawns(client)[0].EntityId),
            "the wolf and its combat state");
        return WolfSpawns(client)[0];
    }

    internal static IReadOnlyList<RealtimeWorldActorSpawn> WolfSpawns(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeWorldActorSpawn>(RealtimeMessageType.WorldActorSpawn, RealtimeProtocol.TryDecodeWorldActorSpawn)
            .Where(value => value.ActorDefinitionId == "mob.feral_wolf")
            .ToArray();
    }

    internal static IReadOnlyList<RealtimeCombatantState> States(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCombatantState>(RealtimeMessageType.CombatantState, RealtimeProtocol.TryDecodeCombatantState);
    }

    internal static IReadOnlyList<RealtimeOwnerCombatState> OwnerStates(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeOwnerCombatState>(RealtimeMessageType.OwnerCombatState, RealtimeProtocol.TryDecodeOwnerCombatState);
    }

    internal static IReadOnlyList<RealtimeCombatEvent> Events(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCombatEventBatch>(RealtimeMessageType.CombatEventBatch, RealtimeProtocol.TryDecodeCombatEventBatch)
            .SelectMany(value => value.Events)
            .ToArray();
    }

    private static readonly Guid BowItemId = Guid.Parse("b0b0b0b0-0000-4000-8000-000000000001");
    private static readonly Guid ArrowItemId = Guid.Parse("a0a0a0a0-0000-4000-8000-000000000002");
    private static readonly Guid StaffItemId = Guid.Parse("5a5a5a5a-0000-4000-8000-000000000003");

    internal static IReadOnlyList<SimulationEquippedItem> ArcherEquipment(int arrows)
    {
        return
        [
            new SimulationEquippedItem("main_hand", BowItemId, Bow, 1, 1),
            new SimulationEquippedItem("ammunition", ArrowItemId, Arrow, arrows, 1)
        ];
    }

    internal static IReadOnlyList<SimulationEquippedItem> CasterEquipment()
    {
        return [new SimulationEquippedItem("main_hand", StaffItemId, Staff, 1, 1)];
    }

    // The fastest bow and staff content allows: an attack every 0.5 s.
    internal static ItemCatalogRuntimeDocument TestCatalog(
        int minimumDamage = 10,
        int maximumDamage = 18,
        float range = 10f,
        int manaCost = 5)
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
                },
                new ItemWeaponFamilyEntry
                {
                    Id = "staff",
                    CombatStyle = CombatStyleIds.Magic,
                    AmmunitionFamily = string.Empty,
                    ProjectileSpeed = 20f
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
                        Range = range,
                        ManaCost = 0
                    }
                },
                new ItemDefinition
                {
                    Id = Staff,
                    Category = ItemCategoryIds.Weapon,
                    Weapon = new ItemWeaponEntry
                    {
                        WeaponFamily = "staff",
                        DamageType = DamageTypeIds.Normal,
                        MinimumDamage = minimumDamage,
                        MaximumDamage = maximumDamage,
                        AttackIntervalSeconds = 0.5f,
                        Range = range,
                        ManaCost = manaCost
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

    internal static CombatRules TestRules(
        float respawnSeconds = 0.5f,
        float batchIntervalSeconds = 5f,
        int maximumMana = 100,
        float combatTimeoutSeconds = 10f,
        int healthRegenerationPerSecond = 0,
        int manaRegenerationPerSecond = 0)
    {
        return CombatRules.FromDocument(new CombatRulesDocument
        {
            formatVersion = CombatRulesFormat.Version,
            player = new PlayerCombatRulesDocument
            {
                maximumHealth = 100,
                maximumMana = maximumMana,
                respawnDelaySeconds = respawnSeconds,
                combatTimeoutSeconds = combatTimeoutSeconds,
                healthRegenerationPerSecond = healthRegenerationPerSecond,
                manaRegenerationPerSecond = manaRegenerationPerSecond,
                equipmentLockoutSeconds = 0.5f
            },
            ammunition = new AmmunitionRulesDocument { batchIntervalSeconds = batchIntervalSeconds },
            ruleSets = [new ShardRuleSetDocument { id = "test", playerVersusPlayer = true }]
        });
    }

    // One wolf a few metres from the World spawn, with a short respawn.
    internal static WorldActorRuntimeDocument WolfNextToTheSpawn(float respawnSeconds, float x = 4f)
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

    internal sealed class LowestRoll : ICombatRandom
    {
        public int NextInclusive(int minimum, int maximum)
        {
            return minimum;
        }
    }
}
