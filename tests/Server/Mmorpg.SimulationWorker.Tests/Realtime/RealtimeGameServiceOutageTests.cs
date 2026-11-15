using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.WorldData.Actors;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// What the realtime loop does while GameService does not answer, over real UDP against the
// scripted GameService. The test turns the availability as a failed or a successful registry
// heartbeat does. Durable client actions are refused at once without a GameService call and
// without holding the lane; movement and combat go on. A death, a quest kill credit, a Mob
// corpse, and an arrow batch wait without spending timeouts and commit once afterwards with the
// same operation id, unless they stay uncertain past the retry horizon, which counts only the
// time GameService answers. The checkpoint goes right after GameService answers again, a join
// with a lease that does not outlast the tolerance is refused, and a stop tells every peer why.
public sealed class RealtimeGameServiceOutageTests
{
    private const string Unavailable = GameServiceAvailability.UnavailableCode;
    private const string Wolves = "quest.fixture.wolves";

    [Fact]
    public async Task EveryDurableIntentIsRefusedAtOnceWhileMovementAndCombatGoOn()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableMobCorpses = true,
            EnableQuests = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: 30f),
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Paused Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);
        GoDown(harness);
        var callsBefore = harness.GameService.Calls.Count;

        var item = RealtimeProtocolRejectionTests.CreateItemIntent();
        client.SendItemIntent(item);
        var corpse = RealtimeProtocolRejectionTests.CreateCorpseIntent();
        client.SendCorpseIntent(corpse);
        var accept = CapabilityAction(archer, RealtimeWorldActorCapabilityKind.Quests, RealtimeCapabilityActionKind.AcceptQuest, Wolves);
        client.SendWorldIntent(accept);
        var gather = CapabilityAction(archer, RealtimeWorldActorCapabilityKind.Gathering, RealtimeCapabilityActionKind.StartGathering, string.Empty);
        client.SendWorldIntent(gather);
        var abandon = RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves);
        client.Send(RealtimeProtocol.EncodeQuestIntent(abandon));
        var lower = RealtimeAppearanceIntent.CreateLowerHeadwear(Guid.NewGuid());
        client.Send(RealtimeProtocol.EncodeAppearanceIntent(lower));
        // Opening an interaction asks GameService nothing, so it is not refused.
        var open = RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            archer.SimulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            wolf.EntityId,
            wolf.RuntimeActorId,
            wolf.InteractionRevision);
        client.SendWorldIntent(open);

        await harness.PollUntilAsync(
            () => client.ItemResults.Count == 1
                && client.CorpseResults.Count == 1
                && client.WorldResults.Count == 3
                && QuestResults(client).Count == 1
                && AppearanceResults(client).Count == 1,
            "every answer");

        Assert.Equal((item.OperationId, Unavailable), (client.ItemResults[0].OperationId, client.ItemResults[0].Error.Code));
        Assert.Equal((corpse.OperationId, Unavailable), (client.CorpseResults[0].OperationId, client.CorpseResults[0].Error.Code));
        Assert.Equal(Unavailable, client.WorldResults.Single(result => result.OperationId == accept.OperationId).Error.Code);
        Assert.Equal(Unavailable, client.WorldResults.Single(result => result.OperationId == gather.OperationId).Error.Code);
        Assert.NotEqual(Unavailable, client.WorldResults.Single(result => result.OperationId == open.OperationId).Error?.Code);
        Assert.Equal((abandon.OperationId, Unavailable), (QuestResults(client)[0].OperationId, QuestResults(client)[0].Error.Code));
        Assert.Equal((lower.OperationId, Unavailable), (AppearanceResults(client)[0].OperationId, AppearanceResults(client)[0].Error.Code));

        // The character kills the wolf, whose live corpse needs no GameService, and walks on, and
        // nothing asks GameService.
        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
        await harness.PollUntilAsync(
            () => harness.LiveMobCorpseStore!.ListActive().Any(value => value.SourceActorRuntimeId == wolf.RuntimeActorId),
            "the live corpse");
        client.SendMoveTo(1, 3f, -1f);
        await harness.PollUntilAsync(
            () => client.OwnerSnapshots.Any(snapshot => snapshot.State.PositionX > 0.1f),
            "the walk");
        Assert.Equal(callsBefore, harness.GameService.Calls.Count);
        Assert.Null(client.ServerDisconnect);

        // No refused action held the lane: the next one runs as soon as GameService answers, after
        // the arrow that waited. Its state is newer than the arrow batch's.
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedItemOperation(request, 5, 0, 200));
        ComeBack(harness);
        var next = RealtimeProtocolRejectionTests.CreateItemIntent();
        client.SendItemIntent(next);
        await harness.PollUntilAsync(() => client.ItemResults.Count == 2, "the next item operation");
        Assert.True(client.ItemResults[1].Succeeded);
        Assert.Single(harness.GameService.AmmunitionConsumptions);
        Assert.Null(client.ServerDisconnect);
    }

    // A queued client action that has not started when GameService stops answering is refused
    // instead of started.
    [Fact]
    public async Task AQueuedActionIsRefusedWhenItsTurnComesWhileGameServiceIsUnavailable()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Queued Hero");
        var gate = new TestGate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return ScriptedGameServiceHandler.CommittedItemOperation(request, 1, 0, 200);
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            var first = RealtimeProtocolRejectionTests.CreateItemIntent();
            var second = RealtimeProtocolRejectionTests.CreateItemIntent();
            client.SendItemIntent(first);
            client.SendItemIntent(second);
            await harness.PollUntilAsync(() => gate.HasEntered, "the first operation at GameService");

            GoDown(harness);
            gate.Release();

            await harness.PollUntilAsync(() => client.ItemResults.Count == 2, "both answers");
            Assert.True(client.ItemResults.Single(result => result.OperationId == first.OperationId).Succeeded);
            Assert.Equal(Unavailable, client.ItemResults.Single(result => result.OperationId == second.OperationId).Error.Code);
            Assert.Equal(1, harness.GameService.ItemOperationCount);
        }
        finally
        {
            gate.Release();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeathCommitsOnceAfterTheOutageWithItsOperation(bool beganBeforeTheOutage)
    {
        await using var harness = DuelHarness();
        var (archerClient, victim, victimClient) = await StartDuelAsync(harness);
        var down = new Flag(beganBeforeTheOutage);
        harness.GameService.OnPlayerDeath = (_, request, _) => down.IsSet
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.DefaultPlayerDeath(request));
        if (!beganBeforeTheOutage)
        {
            GoDown(harness);
        }

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimClient.JoinAccepted!.ControlledEntityId));
        await harness.PollUntilAsync(
            () => CombatTestStates.IsDead(victimClient),
            "the victim to die");
        if (beganBeforeTheOutage)
        {
            await harness.PollUntilAsync(() => harness.GameService.PlayerDeaths.Count == 1, "the attempt whose answer was lost");
            GoDown(harness);
        }

        // Without the outage the death would have retried after half a second.
        await harness.PollForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(beganBeforeTheOutage ? 1 : 0, harness.GameService.PlayerDeaths.Count);

        down.Clear();
        ComeBack(harness);
        var attempts = beganBeforeTheOutage ? 2 : 1;
        await harness.PollUntilAsync(() => harness.GameService.PlayerDeaths.Count == attempts, "the death after the outage");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));

        var deaths = harness.GameService.PlayerDeaths;
        Assert.Equal(attempts, deaths.Count);
        Assert.Single(deaths.Select(death => death.OperationId).Distinct());
        Assert.All(deaths, death => Assert.Equal(victim.CharacterId, death.CharacterId));
        Assert.Null(victimClient.ServerDisconnect);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AKillCreditCountsOnceAfterTheOutageWithItsOperation(bool beganBeforeTheOutage)
    {
        await using var harness = RealtimeQuestCreditTests.Create();
        var archer = RealtimeQuestCreditTests.AddArcher(harness, "Outage Hunter");
        var down = new Flag(beganBeforeTheOutage);
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, RealtimeQuestCreditTests.Hunting(0)));
        harness.GameService.OnQuestOperation = (_, request, _) => down.IsSet
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(RealtimeQuestCreditTests.KillCounted(request, 4, 1));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);
        if (!beganBeforeTheOutage)
        {
            GoDown(harness);
        }

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
        // An attempt asks GameService twice before it reads the quest log again, so the calls are
        // counted once that attempt is over.
        var callsAtOutage = 0;
        if (beganBeforeTheOutage)
        {
            await harness.PollUntilAsync(() => harness.GameService.QuestOperations.Count >= 1, "the attempt whose answer was lost");
            GoDown(harness);
            await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
            callsAtOutage = harness.GameService.QuestOperations.Count;
        }

        await harness.PollForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(callsAtOutage, harness.GameService.QuestOperations.Count);

        down.Clear();
        ComeBack(harness);
        await harness.PollUntilAsync(
            () => RealtimeQuestCreditTests.QuestChanges(client).Any(change => !change.ReloadRequired),
            "the kill to count after the outage");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));

        var credits = harness.GameService.QuestOperations;
        Assert.Equal(callsAtOutage + 1, credits.Count);
        Assert.Single(credits.Select(credit => credit.OperationId).Distinct());
        Assert.Single(RealtimeQuestCreditTests.QuestChanges(client), change => !change.ReloadRequired);
        Assert.True(harness.QuestLogs!.TryGet(archer.CharacterId, archer.SimulationSessionId, out var log));
        Assert.Equal(1, log!.ActiveQuests[Wolves].Progress["wolves"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AMobCorpseIsCreatedOnceAfterTheOutageWithItsOperation(bool beganBeforeTheOutage)
    {
        await using var harness = DurableWolfHarness();
        var archer = harness.AddCharacter("Corpse Hunter") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var down = new Flag(beganBeforeTheOutage);
        harness.GameService.OnDurableMobCorpse = (request, _) => down.IsSet
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.Json(ScriptedGameServiceHandler.DurableMobCorpse(request)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);
        if (!beganBeforeTheOutage)
        {
            GoDown(harness);
        }

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(
            () => Events(client).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == wolf.EntityId),
            "the wolf to die");
        if (beganBeforeTheOutage)
        {
            await harness.PollUntilAsync(() => harness.GameService.DurableMobCorpses.Count == 1, "the attempt whose answer was lost");
            GoDown(harness);
        }

        await harness.PollForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(beganBeforeTheOutage ? 1 : 0, harness.GameService.DurableMobCorpses.Count);
        // The dead wolf waits for its corpse.
        Assert.DoesNotContain(client.WorldActorDespawns, value => value.EntityId == wolf.EntityId);

        down.Clear();
        ComeBack(harness);
        await harness.PollUntilAsync(
            () => client.WorldActorDespawns.Any(value => value.EntityId == wolf.EntityId && value.Reason == "died"),
            "the wolf to leave its corpse");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));

        var creations = harness.GameService.DurableMobCorpses;
        Assert.Equal(beganBeforeTheOutage ? 2 : 1, creations.Count);
        Assert.Single(creations.Select(creation => (creation.OperationId, creation.CorpseId)).Distinct());
        Assert.True(harness.CorpseStore.TryGetActive(creations[0].CorpseId, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnArrowBatchCommitsOnceAfterTheOutageWithItsOperation(bool beganBeforeTheOutage)
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Outage Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Outage Target");
        var down = new Flag(beganBeforeTheOutage);
        harness.GameService.OnAmmunitionConsumption = (_, request, _) => down.IsSet
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(harness.GameService.DefaultAmmunitionConsumption(request));
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => States(archerClient).Any(value => value.EntityId == targetEntityId), "the target");
        if (!beganBeforeTheOutage)
        {
            GoDown(harness);
        }

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        if (beganBeforeTheOutage)
        {
            await harness.PollUntilAsync(() => harness.GameService.AmmunitionConsumptions.Count == 1, "the batch whose answer was lost");
            GoDown(harness);
        }
        else
        {
            await harness.PollUntilAsync(
                () => Events(archerClient).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased) >= 2,
                "shots during the outage");
        }

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        await harness.PollForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(beganBeforeTheOutage ? 1 : 0, harness.GameService.AmmunitionConsumptions.Count);

        down.Clear();
        ComeBack(harness);
        await harness.PollUntilAsync(() => archerClient.Has(RealtimeMessageType.OwnerItemQuantityChanged), "the committed batch");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));

        var batches = harness.GameService.AmmunitionConsumptions;
        var first = batches[0];
        Assert.Equal(beganBeforeTheOutage ? 2 : 1, batches.Count(batch => batch.OperationId == first.OperationId));
        Assert.All(batches.Where(batch => batch.OperationId == first.OperationId), batch => Assert.Equal(first.Quantity, batch.Quantity));
        // Every batch committed once: one arrow count change per batch, and every shot spent once.
        var changes = archerClient.Decode<RealtimeOwnerItemQuantityChanged>(
            RealtimeMessageType.OwnerItemQuantityChanged,
            RealtimeProtocol.TryDecodeOwnerItemQuantityChanged);
        var distinct = batches.DistinctBy(batch => batch.OperationId).ToArray();
        Assert.Equal(distinct.Length, changes.Count);
        Assert.Equal(50 - distinct.Sum(batch => batch.Quantity), changes[^1].Quantity);
    }

    // A death that stays uncertain while GameService answers is given up at the retry horizon:
    // one error line names its operation, and the player's session ends with a code of its own
    // and is released without a final state.
    [Fact]
    public async Task ADeathThatStaysUncertainIsGivenUpAtTheRetryHorizon()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = DuelHarness(clock);
        var (archerClient, victim, victimClient) = await StartDuelAsync(harness);
        harness.GameService.OnPlayerDeath = (_, _, _) => throw new HttpRequestException("The answer was lost.");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimClient.JoinAccepted!.ControlledEntityId));
        await harness.PollUntilAsync(() => harness.GameService.PlayerDeaths.Count == 1, "the first attempt");
        clock.Advance(Horizon);

        await harness.PollUntilAsync(() => victimClient.ServerDisconnect is not null, "the session to end");
        Assert.Equal(PlayerCombatAuthorityCodes.DeathNotSaved, victimClient.ServerDisconnect!.Code);
        var operationId = harness.GameService.PlayerDeaths[0].OperationId;
        var given = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("player_death", given.Message, StringComparison.Ordinal);
        Assert.Contains(operationId.ToString(), given.Message, StringComparison.Ordinal);
        await harness.PollUntilAsync(
            () => harness.GameService.Releases.Any(release => release.SimulationSessionId == victim.SimulationSessionId),
            "the release of the session");
        Assert.Null(harness.GameService.Releases.Single(release => release.SimulationSessionId == victim.SimulationSessionId).Request.FinalState);
    }

    // The horizon counts only the time GameService answers, so an outage of any length never
    // gives an operation up.
    [Fact]
    public async Task TimeWhileGameServiceIsUnavailableDoesNotCountTowardsTheRetryHorizon()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = DuelHarness(clock);
        var (archerClient, _, victimClient) = await StartDuelAsync(harness);
        var down = new Flag(true);
        harness.GameService.OnPlayerDeath = (_, request, _) => down.IsSet
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.DefaultPlayerDeath(request));

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimClient.JoinAccepted!.ControlledEntityId));
        await harness.PollUntilAsync(() => harness.GameService.PlayerDeaths.Count == 1, "the first attempt");
        GoDown(harness);
        clock.Advance(Horizon * 3);
        ComeBack(harness);
        await harness.PollUntilAsync(() => harness.GameService.PlayerDeaths.Count >= 3, "attempts after the outage");

        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Null(victimClient.ServerDisconnect);
        down.Clear();
        await harness.PollUntilAsync(() => CombatTestStates.IsAlive(victimClient), "the death to commit and the respawn");
        Assert.Single(harness.GameService.PlayerDeaths.Select(death => death.OperationId).Distinct());
    }

    [Fact]
    public async Task AKillCreditThatStaysUncertainIsGivenUpAtTheRetryHorizon()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableQuests = true,
            WorldActorDocument = WolfNextToTheSpawn(respawnSeconds: 30f),
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(respawnSeconds: 30f),
            CombatRandom = new LowestRoll(),
            AvailabilityTimeProvider = clock,
            UncertainOperationRetryHorizon = Horizon
        });
        var archer = RealtimeQuestCreditTests.AddArcher(harness, "Unlucky Hunter");
        harness.GameService.OnQuestLog = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.QuestLog(request.CharacterId, 3, RealtimeQuestCreditTests.Hunting(0)));
        harness.GameService.OnQuestOperation = (_, request, _) => request.OperationKind == "credit_quest_kill"
            ? throw new HttpRequestException("The answer was lost.")
            : Task.FromResult(ScriptedGameServiceHandler.CommittedQuestOperation(
                request,
                0,
                4,
                new SimulationQuestChange(Wolves, null, [], 0)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => harness.GameService.QuestOperations.Count >= 1, "the first attempt");
        clock.Advance(Horizon);
        await harness.PollUntilAsync(
            () => harness.Logger.Entries.Any(entry => entry.Level == LogLevel.Error),
            "the credit to be given up");

        var given = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("quest_kill_credit", given.Message, StringComparison.Ordinal);
        Assert.Contains(harness.GameService.QuestOperations[0].OperationId.ToString(), given.Message, StringComparison.Ordinal);
        // The lane goes on: an abandon runs after the dropped credit.
        client.Send(RealtimeProtocol.EncodeQuestIntent(RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), Wolves)));
        await harness.PollUntilAsync(
            () => harness.GameService.QuestOperations.Any(operation => operation.OperationKind == "abandon_quest"),
            "the abandon");
        Assert.Null(client.ServerDisconnect);
    }

    [Fact]
    public async Task AMobCorpseThatStaysUncertainIsGivenUpAtTheRetryHorizon()
    {
        var clock = new SteppedTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = DurableWolfHarness(clock);
        var archer = harness.AddCharacter("Corpse Seeker") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        harness.GameService.OnDurableMobCorpse = (_, _) => throw new HttpRequestException("The answer was lost.");
        await harness.StartAsync();
        var client = await harness.JoinAsync(archer);
        var wolf = await WaitForWolfAsync(harness, client);

        client.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), wolf.EntityId));
        await harness.PollUntilAsync(() => harness.GameService.DurableMobCorpses.Count == 1, "the first attempt");
        clock.Advance(Horizon);

        await harness.PollUntilAsync(
            () => client.WorldActorDespawns.Any(value => value.EntityId == wolf.EntityId),
            "the wolf to leave without its corpse");
        var given = Assert.Single(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("mob_corpse", given.Message, StringComparison.Ordinal);
        Assert.Contains(harness.GameService.DurableMobCorpses[0].OperationId.ToString(), given.Message, StringComparison.Ordinal);
        Assert.False(harness.CorpseStore.TryGetActive(harness.GameService.DurableMobCorpses[0].CorpseId, out _));
    }

    // The checkpoint that came due while GameService did not answer goes right after it answers
    // again, not at the next interval.
    [Fact]
    public async Task TheCheckpointGoesRightAfterGameServiceAnswersAgain()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CharacterCheckpointInterval = TimeSpan.FromSeconds(30)
        });
        var character = harness.AddCharacter("Checkpoint Walker");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        GoDown(harness);
        client.SendMoveTo(1, 3f, -1f);
        await harness.PollUntilAsync(
            () => client.OwnerSnapshots.Any(snapshot => snapshot.State.PositionX > 0.1f),
            "the walk");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(harness.GameService.Checkpoints);

        ComeBack(harness);
        await harness.PollUntilAsync(() => harness.GameService.Checkpoints.Count == 1, "the checkpoint after the outage");
        var saved = Assert.Single(harness.GameService.Checkpoints[0].States);
        Assert.Equal(character.SimulationSessionId, saved.SimulationSessionId);
        Assert.True(saved.PositionX > 0.1f);
    }

    [Fact]
    public async Task AJoinWhoseLeaseDoesNotOutlastTheToleranceIsRefused()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Short Lease Hero") with { SessionLeaseDurationMilliseconds = 60_000 };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();

        var client = harness.Connect(character.Name, character.Ticket);
        await harness.PollUntilAsync(() => client.JoinRejected is not null, "the refused join");

        Assert.Equal("join_failed", client.JoinRejected!.Code);
        var refused = Assert.Single(harness.JoinLogger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("SimulationWorker:GameServiceOutageToleranceSeconds", refused.Message, StringComparison.Ordinal);
        Assert.Contains("SimulationWorker:SimulationSessionHeartbeatSeconds", refused.Message, StringComparison.Ordinal);
        await harness.PollUntilAsync(
            () => harness.GameService.Releases.Any(release => release.SimulationSessionId == character.SimulationSessionId),
            "the release of the refused session");
        Assert.Empty(harness.SessionStore.ListActiveSessions());
    }

    [Fact]
    public async Task ANormalStopTellsEveryPeerThatTheShardShutsDown()
    {
        await using var harness = RealtimeServerHarness.Create();
        var first = harness.AddCharacter("Last First");
        var second = harness.AddCharacter("Last Second");
        await harness.StartAsync();
        var firstClient = await harness.JoinAsync(first);
        var secondClient = await harness.JoinAsync(second);

        await harness.StopWhilePollingAsync();

        Assert.Equal(SimulationWorkerStopReason.ServerShutdownCode, firstClient.ServerDisconnect?.Code);
        Assert.Equal(SimulationWorkerStopReason.ServerShutdownCode, secondClient.ServerDisconnect?.Code);
        Assert.Single(harness.GameService.Checkpoints);
    }

    // After the outage tolerance the peers learn why the worker stops, and nothing that would only
    // spend its timeout goes to GameService on the way out.
    [Fact]
    public async Task AStopAfterTheToleranceTellsEveryPeerWhyAndSendsNothingToGameService()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Stranded Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Stranded Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => States(archerClient).Any(value => value.EntityId == targetEntityId), "the target");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => Events(archerClient).Any(value => value.Kind == RealtimeCombatEventKind.AttackReleased),
            "a shot");
        GoDown(harness);
        harness.StopReason.Set(Unavailable, GameServiceAvailability.UnavailableMessage);
        var callsBefore = harness.GameService.Calls.Count;

        await harness.StopWhilePollingAsync();

        Assert.Equal(Unavailable, archerClient.ServerDisconnect?.Code);
        Assert.Equal(Unavailable, targetClient.ServerDisconnect?.Code);
        Assert.Equal(callsBefore, harness.GameService.Calls.Count);
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("are not saved at shutdown", StringComparison.Ordinal));
        Assert.Contains(harness.Logger.Entries, entry => entry.Message.Contains("are not committed at shutdown", StringComparison.Ordinal));
    }

    private static readonly TimeSpan Horizon = TimeSpan.FromSeconds(600);

    // Turns GameService unavailable, as a registry heartbeat that cannot reach it does.
    private static void GoDown(RealtimeServerHarness harness)
    {
        harness.Availability.HeartbeatFailed("auth_service_unavailable");
    }

    // Turns GameService available again, as the next successful registry heartbeat does.
    private static void ComeBack(RealtimeServerHarness harness)
    {
        harness.Availability.HeartbeatSucceeded();
    }

    private static RealtimeServerHarness DuelHarness(TimeProvider? clock = null)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 500, maximumDamage: 500),
            CombatRules = TestRules(respawnSeconds: 0.5f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll(),
            AvailabilityTimeProvider = clock,
            UncertainOperationRetryHorizon = Horizon
        });
    }

    private static async Task<(RealtimeRecordingClient Archer, RealtimeTestCharacter Victim, RealtimeRecordingClient VictimClient)>
        StartDuelAsync(RealtimeServerHarness harness)
    {
        var archer = harness.AddCharacter("Outage Duelist") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Outage Victim");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => States(archerClient).Any(value => value.EntityId == victimEntityId), "the victim");
        return (archerClient, victim, victimClient);
    }

    // A wolf next to the spawn whose corpse GameService keeps.
    private static RealtimeServerHarness DurableWolfHarness(TimeProvider? clock = null)
    {
        var document = WolfNextToTheSpawn(respawnSeconds: 30f);
        document.Catalog.Actors.Single(actor => actor.Id == "mob.feral_wolf").CorpsePersistenceMode =
            WorldActorCorpsePersistenceModeIds.Durable;
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            EnableMobCorpses = true,
            WorldActorDocument = document,
            ItemCatalog = TestCatalog(minimumDamage: 100, maximumDamage: 100),
            CombatRules = TestRules(),
            CombatRandom = new LowestRoll(),
            AvailabilityTimeProvider = clock,
            UncertainOperationRetryHorizon = Horizon
        });
    }

    // A capability action at a made-up actor, which is refused before any lease check.
    private static RealtimeWorldInteractionIntent CapabilityAction(
        RealtimeTestCharacter character,
        RealtimeWorldActorCapabilityKind kind,
        RealtimeCapabilityActionKind action,
        string questId)
    {
        return RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            character.SimulationSessionId,
            Guid.NewGuid(),
            RealtimeWorldInteractionTargetKind.WorldActor,
            1,
            Guid.NewGuid(),
            1,
            "capability",
            kind,
            1,
            action,
            questId: questId);
    }

    private static IReadOnlyList<RealtimeQuestIntentResult> QuestResults(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeQuestIntentResult>(RealtimeMessageType.QuestIntentResult, RealtimeProtocol.TryDecodeQuestIntentResult);
    }

    private static IReadOnlyList<RealtimeAppearanceIntentResult> AppearanceResults(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAppearanceIntentResult>(RealtimeMessageType.AppearanceIntentResult, RealtimeProtocol.TryDecodeAppearanceIntentResult);
    }

    // A switch that the scripted GameService reads from another thread.
    private sealed class Flag(bool set)
    {
        private int value = set ? 1 : 0;

        public bool IsSet => Volatile.Read(ref value) == 1;

        public void Clear()
        {
            Volatile.Write(ref value, 0);
        }
    }

    private static class CombatTestStates
    {
        public static bool IsDead(RealtimeRecordingClient client)
        {
            return OwnerStates(client).Any(value => value.LifeState == RealtimeCombatLifeState.Dead);
        }

        public static bool IsAlive(RealtimeRecordingClient client)
        {
            return OwnerStates(client).Any(value => value.LifeState == RealtimeCombatLifeState.Alive && value.LifeSequence == 2);
        }
    }

    private static class PlayerCombatAuthorityCodes
    {
        public const string DeathNotSaved = "death_not_saved";
    }
}
