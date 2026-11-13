using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// An operation that a player sent before its connection ended still commits at GameService, so
// what it committed reaches the character while the worker holds its session: a lingering body
// builds its spent arrows and its look on it, and a player who came back gets the owner updates.
// Only the operation's own result needs the peer that sent it. A session that ended takes nothing.
public sealed class RealtimeInFlightOperationTests
{
    private const long CommittedRevision = 7;

    // Shots fired while an item operation runs stay pending. When the connection ends before the
    // operation's answer, the body's batch must build on the revision the operation committed,
    // which GameService requires, so the spent arrows commit instead of being dropped.
    [Fact]
    public async Task ShotsFiredDuringAnItemOperationCommitOnItsRevisionAfterTheConnectionEnded()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Juggling Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Juggled Target");
        using var gate = new Gate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.Task;
            return Committed(request, CommittedRevision, ArcherEquipment(50));
        };

        // GameService takes spent arrows only on the character's current revision.
        harness.GameService.OnAmmunitionConsumption = (_, request, _) => Task.FromResult(
            request.ExpectedCharacterRevision == CommittedRevision
                ? harness.GameService.DefaultAmmunitionConsumption(request)
                : ScriptedGameServiceHandler.Problem(
                    HttpStatusCode.Conflict,
                    "item_state_conflict",
                    "The character's item state moved on."));
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var archerEntityId = archerClient.JoinAccepted!.ControlledEntityId;
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");

        // Destroying a carried item changes no equipment slot, so the archer shoots while it runs.
        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 1));
        await harness.PollUntilAsync(() => harness.GameService.ItemOperationCount == 1, "the item operation at GameService");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => Events(archerClient).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased) >= 2,
            "shots while the operation runs");
        var seenShots = Events(archerClient).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased);

        archerClient.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(archerEntityId, out _),
            "the body to stay without a connection");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Empty(harness.GameService.AmmunitionConsumptions);

        gate.Open();

        await harness.PollUntilAsync(
            () => harness.GameService.AmmunitionConsumptions.Count > 0,
            "the batch of the body");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        var batch = Assert.Single(harness.GameService.AmmunitionConsumptions);
        Assert.Equal(CommittedRevision, batch.ExpectedCharacterRevision);
        Assert.True(batch.Quantity >= seenShots);
        Assert.True(harness.CharacterItemStateStore.TryGetItemState(
            archer.CharacterId,
            archer.SimulationSessionId,
            out var state));
        Assert.Equal(CommittedRevision + 1, state!.ItemStateRevision);
        Assert.Equal(50 - batch.Quantity, state.FindEquipped("ammunition")!.Quantity);
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
    }

    // A body whose time is up while the operation that its player sent last still runs waits for
    // the answer, so what the operation committed still finds it, and its spent arrows commit on
    // that revision before its session is released.
    [Fact]
    public async Task ABodyWaitsForTheAnswerToTheOperationItsPlayerSentLast()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1),
            CombatRules = TestRules(batchIntervalSeconds: 60f),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll(),
            LingeringBodySeconds = 1
        });
        var archer = harness.AddCharacter("Slow Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Slow Target");
        using var gate = new Gate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.Task;
            return Committed(request, CommittedRevision, ArcherEquipment(50));
        };
        var releasesAtConsumption = -1;
        harness.GameService.OnAmmunitionConsumption = (_, request, _) =>
        {
            releasesAtConsumption = harness.GameService.ReleaseCount;
            return Task.FromResult(
                request.ExpectedCharacterRevision == CommittedRevision
                    ? harness.GameService.DefaultAmmunitionConsumption(request)
                    : ScriptedGameServiceHandler.Problem(
                        HttpStatusCode.Conflict,
                        "item_state_conflict",
                        "The character's item state moved on."));
        };
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var archerEntityId = archerClient.JoinAccepted!.ControlledEntityId;
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");
        archerClient.SendItemIntent(RealtimeItemOperationIntent.CreateDestroy(Guid.NewGuid(), 0, Guid.NewGuid(), 1));
        await harness.PollUntilAsync(() => harness.GameService.ItemOperationCount == 1, "the item operation at GameService");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(
            () => Events(archerClient).Any(value => value.Kind == RealtimeCombatEventKind.AttackReleased),
            "a shot while the operation runs");
        archerClient.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(archerEntityId, out _),
            "the body to stay without a connection");

        // Past the body's time it still stands, since the answer has not arrived.
        await harness.PollForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        Assert.Empty(harness.GameService.Releases);

        gate.Open();

        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the body's session");
        var batch = Assert.Single(harness.GameService.AmmunitionConsumptions);
        Assert.Equal(CommittedRevision, batch.ExpectedCharacterRevision);
        Assert.Equal(0, releasesAtConsumption);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
    }

    // An equip that GameService commits after the connection ended dresses the body: others see
    // the new look, and its item and carry state are what GameService committed.
    [Fact]
    public async Task AnEquipmentChangeThatWasInFlightWhenTheConnectionEndedDressesTheBody()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules()
        });
        var watcherCharacter = harness.AddCharacter("Fitting Watcher");
        var hero = harness.AddCharacter("Dressing Hero");
        var bow = ArcherEquipment(50)[0];
        using var gate = new Gate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.Task;
            return Committed(request, 3, [bow], carriedWeight: 15);
        };
        await harness.StartAsync();
        var watcher = await harness.JoinAsync(watcherCharacter);
        var heroClient = await harness.JoinAsync(hero);
        var heroEntityId = heroClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => watcher.Spawns.Any(spawn => spawn.EntityId == heroEntityId),
            "the watcher to see the hero");
        Assert.Empty(watcher.Spawns.Single(spawn => spawn.EntityId == heroEntityId).Appearance.Equipment);

        heroClient.SendItemIntent(RealtimeItemOperationIntent.CreateEquip(Guid.NewGuid(), 0, bow.ItemInstanceId, 1, "main_hand"));
        await harness.PollUntilAsync(() => harness.GameService.ItemOperationCount == 1, "the equip at GameService");
        heroClient.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(heroEntityId, out _),
            "the body to stay without a connection");

        gate.Open();

        await harness.PollUntilAsync(
            () => Looks(watcher).Any(change => change.EntityId == heroEntityId && change.Appearance.Equipment.Length > 0),
            "the new look of the body");
        var look = Looks(watcher).Last(change => change.EntityId == heroEntityId).Appearance;
        var shown = Assert.Single(look.Equipment);
        Assert.Equal(("main_hand", bow.DefinitionId), (shown.EquipmentSlotId, shown.DefinitionId));
        Assert.True(harness.CharacterItemStateStore.TryGetItemState(hero.CharacterId, hero.SimulationSessionId, out var state));
        Assert.Equal(3, state!.ItemStateRevision);
        Assert.Equal(bow.ItemInstanceId, state.FindEquipped("main_hand")!.ItemInstanceId);
        Assert.True(harness.EntityRegistry.TryGetPlayerByCharacterId(hero.CharacterId, out var body));
        Assert.Equal((3L, 15L), (body!.Movement.CarryState.ItemStateRevision, body.Movement.CarryState.CarriedWeight));
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
    }

    // A session that GameService revoked while the operation ran took its body with it, so the
    // answer that arrives after it changes nothing.
    [Fact]
    public async Task TheAnswerToAnOperationOfASessionThatEndedChangesNothing()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules()
        });
        var watcherCharacter = harness.AddCharacter("Revocation Watcher");
        var hero = harness.AddCharacter("Revoked Packer");
        using var gate = new Gate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.Task;
            return Committed(request, CommittedRevision, [ArcherEquipment(50)[0]], carriedWeight: 15);
        };
        await harness.StartAsync();
        var watcher = await harness.JoinAsync(watcherCharacter);
        var heroClient = await harness.JoinAsync(hero);
        var heroEntityId = heroClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => watcher.Spawns.Any(spawn => spawn.EntityId == heroEntityId),
            "the watcher to see the hero");
        heroClient.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
        await harness.PollUntilAsync(() => harness.GameService.ItemOperationCount == 1, "the item operation at GameService");
        heroClient.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(heroEntityId, out _),
            "the body to stay without a connection");
        Assert.True(harness.SessionStore.Invalidate(
            hero.CharacterId,
            hero.SimulationSessionId,
            hero.SimulationSessionToken,
            "session_revoked",
            "The simulation session ended."));
        await harness.PollUntilAsync(
            () => watcher.Despawns.Any(despawn => despawn.EntityId == heroEntityId),
            "the body to go");
        var looksBefore = Looks(watcher).Count;

        gate.Open();
        await harness.PollForAsync(TimeSpan.FromMilliseconds(500));

        Assert.False(harness.CharacterItemStateStore.TryGetItemState(hero.CharacterId, hero.SimulationSessionId, out _));
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
        Assert.Equal(looksBefore, Looks(watcher).Count);
        Assert.Empty(harness.GameService.Releases);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    // The player came back while the operation ran: the committed change reaches the character and
    // the new client gets the owner updates. The operation's result does not, since the client
    // ended every operation it waited for when its session ended, and a result for an operation it
    // does not know makes it report a mismatch and read its whole inventory again.
    [Fact]
    public async Task APlayerWhoCameBackGetsTheChangeThatItsBodyWaitedFor()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules()
        });
        var hero = harness.AddCharacter("Returning Packer");
        harness.GameService.AddCharacter(hero);
        var containerId = Guid.NewGuid();
        var movedItemId = Guid.NewGuid();
        using var gate = new Gate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.Task;
            return ScriptedGameServiceHandler.Json(new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [new SimulationItemCharacterRevision(request.CharacterId, 1, 40, 200, 125)],
                [new SimulationItemContainerRevision(containerId, 4)],
                [],
                [],
                CharacterStateTestData.Create(request.CharacterId, 1, 40, 200),
                [new SimulationItemChange(movedItemId, false, "potion", 3, 8, containerId, 2, null, null, [])]));
        };
        await harness.StartAsync();
        var first = await harness.JoinAsync(hero);
        var entityId = first.JoinAccepted!.ControlledEntityId;
        var intent = RealtimeProtocolRejectionTests.CreateItemIntent();
        first.SendItemIntent(intent);
        await harness.PollUntilAsync(() => harness.GameService.ItemOperationCount == 1, "the item operation at GameService");
        first.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");
        var reconnect = hero with { Ticket = "ticket-returning-packer", SimulationSessionToken = "returned-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "Returning Packer again");
        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.Equal(entityId, second.JoinAccepted.ControlledEntityId);

        gate.Open();

        await harness.PollUntilAsync(
            () => second.Has(RealtimeMessageType.OwnerItemStateChanged)
                && CarryStates(second).Any(carry => carry.ItemStateRevision == 1),
            "the owner updates");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        var change = Assert.Single(second.Decode<RealtimeOwnerItemStateChanged>(
            RealtimeMessageType.OwnerItemStateChanged,
            RealtimeProtocol.TryDecodeOwnerItemStateChanged));
        Assert.Equal(intent.OperationId, change.OperationId);
        Assert.Equal(1, change.CarryState.ItemStateRevision);
        Assert.Equal(40, CarryStates(second).Last().CarriedWeight);
        Assert.Empty(second.ItemResults);
        Assert.True(harness.CharacterItemStateStore.TryGetItemState(hero.CharacterId, hero.SimulationSessionId, out var state));
        Assert.Equal(1, state!.ItemStateRevision);
        Assert.True(harness.SessionStore.TryGet(hero.CharacterId, out var current));
        Assert.Equal(("returned-token", 40L), (current!.SimulationSessionToken, current.CarryState.CarriedWeight));
    }

    // The fence holds while the body still stands: a session that ended takes nothing even before
    // the body timer took the body out.
    [Fact]
    public void AnAnswerForASessionThatEndedIsDroppedWhileItsBodyStillStands()
    {
        using var fixture = new RealtimeLoopFixture();
        using var gameService = new FixtureGameService();
        var handler = fixture.CreateItemHandler(gameService.Operations(fixture));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        Assert.True(fixture.Teardown.LeaveBody(player.Context, player.Session));
        Assert.True(fixture.SessionStore.Invalidate(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            player.Session.SimulationSessionToken,
            "session_revoked",
            "The simulation session ended."));

        handler.Complete(new ItemOperationCompleted(player.Context, player.Session, CommittedDestroy(player.Session, 5)));

        Assert.True(fixture.CharacterItemStateStore.TryGetItemState(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            out var state));
        Assert.Equal(0, state!.ItemStateRevision);
        Assert.False(player.Context.AuthorityOperationInFlight);
        Assert.Empty(player.Peer.Sent);
        Assert.Equal(1, fixture.Entities.PlayerCount);
    }

    // A body has no peer to disconnect, so an answer that ends its session invalidates the session,
    // and the body timer takes the body out without a release.
    [Fact]
    public void AnAnswerThatEndsTheSessionOfABodyInvalidatesIt()
    {
        using var fixture = new RealtimeLoopFixture();
        using var gameService = new FixtureGameService();
        var handler = fixture.CreateItemHandler(gameService.Operations(fixture));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        Assert.True(fixture.Teardown.LeaveBody(player.Context, player.Session));

        handler.Complete(new ItemOperationCompleted(
            player.Context,
            player.Session,
            SimulationItemInteractionResult.Rejected(
                RealtimeProtocolRejectionTests.CreateItemIntent(),
                "simulation_session_invalid",
                "The simulation session is invalid, released, or expired.",
                shouldDisconnect: true)));

        Assert.False(fixture.SessionStore.IsCurrent(player.Session, DateTime.UtcNow));
        Assert.True(fixture.SessionStore.TryTakeInvalidation(player.Session.SimulationSessionId, out var invalidation));
        Assert.Equal("simulation_session_invalid", invalidation!.Code);
        Assert.Empty(player.Peer.Sent);
    }

    // A reconnect moved the session to a new token while the operation ran: its committed state
    // still reaches the character, but its failure ends nothing, since the session went on with
    // what GameService held at the reconnect.
    [Fact]
    public void AnAnswerOfAnEarlierGenerationAppliesItsStateButEndsNothing()
    {
        using var fixture = new RealtimeLoopFixture();
        using var gameService = new FixtureGameService();
        var handler = fixture.CreateItemHandler(gameService.Operations(fixture));
        var player = fixture.ConnectPlayer(1);
        player.Context.AuthorityOperationInFlight = true;
        Assert.True(fixture.Teardown.LeaveBody(player.Context, player.Session));
        Assert.Equal(
            ActiveSimulationSessionRegistration.Reconnected,
            fixture.SessionStore.Register(player.Session with { SimulationSessionToken = "newer-token" }));

        handler.Complete(new ItemOperationCompleted(
            player.Context,
            player.Session,
            SimulationItemInteractionResult.Rejected(
                RealtimeProtocolRejectionTests.CreateItemIntent(),
                "simulation_session_invalid",
                "The simulation session credentials or Shard binding are invalid.",
                shouldDisconnect: true)));
        player.Context.AuthorityOperationInFlight = true;
        handler.Complete(new ItemOperationCompleted(player.Context, player.Session, CommittedDestroy(player.Session, 5)));

        Assert.True(fixture.SessionStore.TryGet(player.Session.CharacterId, out var current));
        Assert.Equal("newer-token", current!.SimulationSessionToken);
        Assert.True(fixture.SessionStore.IsCurrent(current, DateTime.UtcNow));
        Assert.Equal(5, current.CarryState.ItemStateRevision);
        Assert.True(fixture.CharacterItemStateStore.TryGetItemState(
            player.Session.CharacterId,
            player.Session.SimulationSessionId,
            out var state));
        Assert.Equal(5, state!.ItemStateRevision);
        Assert.Equal(5, player.Entity.Movement.CarryState.ItemStateRevision);
        Assert.Empty(player.Peer.Sent);
        Assert.Equal(1, fixture.Entities.PlayerCount);
    }

    private static HttpResponseMessage Committed(
        SimulationItemOperationRequest request,
        long revision,
        IReadOnlyList<SimulationEquippedItem> equipment,
        long carriedWeight = 0)
    {
        return ScriptedGameServiceHandler.Json(new SimulationItemTransactionResponse(
            request.OperationId,
            request.OperationKind,
            true,
            null,
            [new SimulationItemCharacterRevision(request.CharacterId, revision, carriedWeight, 200)],
            [],
            [],
            [],
            CharacterStateTestData.Create(request.CharacterId, revision, carriedWeight, 200, equipment)));
    }

    private static SimulationItemInteractionResult CommittedDestroy(ActiveSimulationSession session, long revision)
    {
        var intent = RealtimeProtocolRejectionTests.CreateItemIntent();
        return SimulationItemInteractionResult.Committed(
            intent,
            new SimulationItemTransactionResponse(
                intent.OperationId,
                "destroy",
                true,
                null,
                [new SimulationItemCharacterRevision(session.CharacterId, revision, 0, 200)],
                [],
                [],
                [],
                CharacterStateTestData.Create(session.CharacterId, revision)));
    }

    private static IReadOnlyList<RealtimeEntityAppearanceChanged> Looks(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeEntityAppearanceChanged>(
            RealtimeMessageType.EntityAppearanceChanged,
            RealtimeProtocol.TryDecodeEntityAppearanceChanged);
    }

    private static IReadOnlyList<RealtimeCarryState> CarryStates(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeCarryState>(
            RealtimeMessageType.CarryStateChanged,
            RealtimeProtocol.TryDecodeCarryStateChanged);
    }

    // Holds GameService's answer until the test opens it. Declared after the harness, it opens on
    // dispose before the harness stops, so a failed assertion never leaves the stop waiting for an
    // answer that never comes.
    private sealed class Gate : IDisposable
    {
        private readonly TaskCompletionSource source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => source.Task;

        public void Open()
        {
            source.TrySetResult();
        }

        public void Dispose()
        {
            Open();
        }
    }

    // The item operations of a loop fixture, over a scripted GameService that no test here calls.
    private sealed class FixtureGameService : IDisposable
    {
        private readonly HttpClient httpClient = new(new ScriptedGameServiceHandler())
        {
            BaseAddress = new Uri("http://game-service.test")
        };

        public ItemAuthorityOperations Operations(RealtimeLoopFixture fixture)
        {
            return new ItemAuthorityOperations(
                new SimulationItemInteractionService(new GameServiceClient(httpClient)),
                fixture.Completions,
                NullLogger.Instance);
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }
    }
}
