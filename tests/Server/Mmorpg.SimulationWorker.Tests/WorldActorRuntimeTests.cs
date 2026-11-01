using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class WorldActorRuntimeTests
{
    [Fact]
    public void AssignmentActivationReconstructsActorsWithFreshRuntimeIdentities()
    {
        var runtime = WorldActorTestData.Compile();
        var firstRegistry = new SimulationEntityRegistry();
        var secondRegistry = new SimulationEntityRegistry();
        var first = new WorldActorStore(runtime, firstRegistry);
        var second = new WorldActorStore(runtime, secondRegistry);

        var firstActors = first.ActivateAssignment(
            runtime.WorldId,
            "local-shard-1",
            "runtime-one");
        var secondActors = second.ActivateAssignment(
            runtime.WorldId,
            "local-shard-1",
            "runtime-two");

        Assert.Equal(runtime.SpawnInstances.Length, firstActors.Count);
        Assert.Equal(
            firstActors.Select(actor => actor.Spawn.Id),
            secondActors.Select(actor => actor.Spawn.Id));
        Assert.Empty(
            firstActors.Select(actor => actor.RuntimeActorId)
                .Intersect(secondActors.Select(actor => actor.RuntimeActorId)));
        Assert.Equal(
            firstActors.Select(actor => actor.NetworkEntityId),
            secondActors.Select(actor => actor.NetworkEntityId));
        Assert.All(firstActors, actor =>
        {
            Assert.NotEqual(Guid.Empty, actor.RuntimeActorId);
            Assert.NotEqual(0ul, actor.NetworkEntityId);
            Assert.NotEqual(actor.Definition.Id, actor.Spawn.SpawnDefinitionId);
        });
    }

    [Fact]
    public void PlayerAndActorNetworkIdsShareOneAllocatorWithoutCollision()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "test-runtime");
        var player = RegisterPlayer(registry, CreateSession(), 0f, -1f);

        Assert.DoesNotContain(player.NetworkEntityId, actors.Select(actor => actor.NetworkEntityId));
        Assert.True(player.NetworkEntityId > actors.Max(actor => actor.NetworkEntityId));
    }

    [Fact]
    public void ActorPresenceUsesExistingInterestEnterAndExitLifecycle()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = actorStore.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "test-runtime")
            .Single(value => value.Definition.Id == "npc.city_services");
        var player = RegisterPlayer(registry, CreateSession(), 0f, -1f);
        var interest = new SimulationInterestManager(
            new InterestManagementConfig(16f, 32f, 36f));
        interest.Rebuild(new[]
        {
            new SimulationInterestEntity(player.NetworkEntityId, 0f, -1f),
            new SimulationInterestEntity(
                actor.NetworkEntityId,
                actor.PositionX,
                actor.PositionZ)
        });

        var entered = interest.Refresh(91, player.NetworkEntityId);
        Assert.Contains(actor.NetworkEntityId, entered.Entered);

        Assert.True(interest.IsVisibleToAnyConnection(actor.NetworkEntityId));

        interest.UpdateEntity(new SimulationInterestEntity(actor.NetworkEntityId, 200f, 200f));
        var exited = interest.Refresh(91, player.NetworkEntityId);
        Assert.Contains(actor.NetworkEntityId, exited.Exited);
        Assert.False(interest.IsVisibleToAnyConnection(actor.NetworkEntityId));
    }

    [Fact]
    public void DespawnRetainsBoundedRuntimeIdentityForReliablePresenceExit()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = actorStore.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "test-runtime")
            .Single(value => value.Definition.Id == "npc.city_services");

        Assert.True(actorStore.TryDespawn(actor.NetworkEntityId, out var despawned));
        Assert.Same(actor, despawned);
        Assert.False(actorStore.TryGetByEntityId(actor.NetworkEntityId, out _));
        Assert.True(
            actorStore.TryGetDespawnedByEntityId(actor.NetworkEntityId, out var tombstone));
        Assert.Same(actor, tombstone);
        Assert.Equal(actor.RuntimeActorId, tombstone!.RuntimeActorId);

        actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "replacement-runtime");
        Assert.False(actorStore.TryGetDespawnedByEntityId(actor.NetworkEntityId, out _));

        // A tombstone stays while a connection still sees the entity and leaves after.
        var replacement = actorStore.ListActors()[0];
        Assert.True(actorStore.TryDespawn(replacement.NetworkEntityId, out _));
        Assert.Equal(0, actorStore.PruneDespawned(_ => true));
        Assert.Equal(1, actorStore.DespawnedCount);
        Assert.Equal(1, actorStore.PruneDespawned(_ => false));
        Assert.Equal(0, actorStore.DespawnedCount);
        Assert.False(actorStore.TryGetDespawnedByEntityId(replacement.NetworkEntityId, out _));
    }

    [Fact]
    public void CentralSchedulerTransitionsOnlyMobsAndOwnsNoPerActorLoops()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        actorStore.ActivateAssignment("development-world-1", "local-shard-1", "test-runtime");
        var scheduler = new WorldActorActivityScheduler(actorStore);
        var player = RegisterPlayer(registry, CreateSession(), 0f, -1f);

        Assert.Empty(scheduler.Evaluate(Array.Empty<PlayerSimulationEntity>(), 1, 30));
        Assert.Empty(scheduler.Evaluate(new[] { player }, 59, 30));
        var changed = scheduler.Evaluate(new[] { player }, 60, 30);

        Assert.Equal(2, changed.Count);
        Assert.All(changed, actor =>
        {
            Assert.Equal(WorldActorKindIds.Mob, actor.Definition.Kind);
            Assert.Equal(WorldActorActivityTierIds.Active, actor.ActivityTier);
        });
        Assert.All(
            actorStore.ListActors().Where(actor => actor.Definition.Kind == WorldActorKindIds.Npc),
            actor => Assert.Equal(WorldActorActivityTierIds.EventDriven, actor.ActivityTier));
        Assert.DoesNotContain(
            typeof(WorldActorRuntimeState).GetFields(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public),
            field => typeof(Task).IsAssignableFrom(field.FieldType)
                || typeof(Timer).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void CapabilitySummariesAreComposedAndFilteredPerPlayer()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = actorStore.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "test-runtime")
            .Single(value => value.Definition.Id == "npc.city_services");
        var first = CreateSession();
        var second = CreateSession();
        var capabilityRegistry = new WorldActorCapabilityRegistry(
            new CharacterFilteredCapabilityPolicy(first.CharacterId));

        var firstSummary = capabilityRegistry.BuildSummary(first, actor);
        var secondSummary = capabilityRegistry.BuildSummary(second, actor);

        Assert.All(firstSummary.Capabilities, capability => Assert.True(capability.IsAvailable));
        Assert.All(secondSummary.Capabilities, capability => Assert.False(capability.IsAvailable));
        Assert.NotEqual(firstSummary.Revision, secondSummary.Revision);
    }

    [Fact]
    public async Task CapabilityDispatchUsesTypedHandlersAndDeferredDefaults()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = actorStore.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "test-runtime")
            .Single(value => value.Definition.Id == "npc.city_services");
        var session = CreateSession();
        var capabilityRegistry = new WorldActorCapabilityRegistry(
            new DefaultWorldActorCapabilityAvailabilityPolicy(),
            new[] { new TestVendorCapabilityHandler() });

        Assert.Equal(
            Enum.GetValues<RealtimeWorldActorCapabilityKind>(),
            capabilityRegistry.RegisteredKinds.ToArray());
        var dialogue = actor.Definition.Capabilities.Single(value =>
            value.Kind == WorldActorCapabilityKindIds.Dialogue);
        var vendor = actor.Definition.Capabilities.Single(value =>
            value.Kind == WorldActorCapabilityKindIds.Vendor);

        var deferred = await capabilityRegistry.DispatchAsync(
            session,
            actor,
            dialogue,
            CreateCapabilityIntent(session, actor, dialogue),
            CancellationToken.None);
        var handled = await capabilityRegistry.DispatchAsync(
            session,
            actor,
            vendor,
            CreateCapabilityIntent(session, actor, vendor),
            CancellationToken.None);

        Assert.False(deferred.Succeeded);
        Assert.Equal("world_interaction_capability_deferred", deferred.Code);
        Assert.True(handled.Succeeded);
        Assert.Equal(string.Empty, handled.Code);
    }

    [Fact]
    public void CapabilityRegistryRejectsAmbiguousDuplicateHandlers()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new WorldActorCapabilityRegistry(
                new DefaultWorldActorCapabilityAvailabilityPolicy(),
                new IWorldActorCapabilityHandler[]
                {
                    new TestVendorCapabilityHandler(),
                    new TestVendorCapabilityHandler()
                }));
    }

    [Fact]
    public void AssignmentContentMustFitRealtimePacketsBeforeUdpAdmission()
    {
        var authoring = WorldActorTestData.LoadActors();
        var actor = authoring.Actors.Single(value => value.Id == "npc.city_services");
        actor.Capabilities = Enumerable.Range(0, 16)
            .Select(index => new WorldActorCapabilityAuthoringEntry
            {
                Id = "services.oversized_" + index.ToString("D2"),
                Kind = WorldActorCapabilityKindIds.Dialogue,
                DisplayName = new string('\u00e5', 128),
                MenuOption = "Talk-to"
            })
            .ToArray();
        var runtime = WorldActorCompiler.Compile(
            authoring,
            WorldActorTestData.LoadSpawns());
        var store = new WorldActorStore(runtime, new SimulationEntityRegistry());
        var actors = store.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "test-runtime");

        var exception = Assert.Throws<InvalidDataException>(
            () => WorldActorProtocolCompatibility.Validate(actors));

        Assert.Contains("realtime protocol", exception.Message, StringComparison.Ordinal);
    }

    // The spawn names every capability in authored order, so the client can offer the
    // right-click menu before the interaction opens.
    [Fact]
    public void ActorSpawnCarriesExamineTextAndMenuOptionsInAuthoredOrder()
    {
        var store = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry());
        var actors = store.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "test-runtime");
        var mira = actors.First(value => value.Definition.Id == "npc.city_services");
        var wolf = actors.First(value => value.Definition.Id == "mob.feral_wolf");

        var miraSpawn = WorldActorProtocolMapper.ToSpawn(mira);
        var wolfSpawn = WorldActorProtocolMapper.ToSpawn(wolf);

        Assert.Equal(mira.Definition.ExamineText, miraSpawn.ExamineText);
        Assert.Equal(
            [
                "services.dialogue:Dialogue:Talk-to",
                "services.vendor:Vendor:Trade",
                "services.bank:Bank:Bank",
                "services.recovery:RecoveryStorage:Recover",
                "services.insurance:Insurance:Insure",
                "services.quest_offer:QuestOffer:Quests",
                "services.quest_turn_in:QuestTurnIn:Turn-in",
                "services.crafting:Crafting:Craft"
            ],
            miraSpawn.MenuOptions.Select(option => option.CapabilityId + ":" + option.Kind + ":" + option.Label));
        Assert.Equal("A lean, hungry wolf. It eyes you like a meal.", wolfSpawn.ExamineText);
        Assert.Empty(wolfSpawn.MenuOptions);
        WorldActorProtocolCompatibility.Validate(actors);
    }

    // A Mob's nameplate shows its level and whether it attacks on sight, so the spawn carries
    // both; a respawned Mob keeps them.
    [Fact]
    public void ActorSpawnCarriesTheMobLevelAndAggression()
    {
        var store = new WorldActorStore(
            WorldActorTestData.CompileWorld("development-world-2"),
            new SimulationEntityRegistry());
        var actors = store.ActivateAssignment(
            "development-world-2",
            "local-shard-1",
            "test-runtime");

        RealtimeWorldActorSpawn SpawnOf(string actorId) =>
            WorldActorProtocolMapper.ToSpawn(actors.First(value => value.Definition.Id == actorId));

        var feralWolf = SpawnOf("mob.feral_wolf");
        var alpha = SpawnOf("mob.feral_alpha");
        var greyWolf = SpawnOf("mob.grey_wolf");
        var mira = SpawnOf("npc.city_services");
        var vein = SpawnOf("resource.iron_vein");
        Assert.Equal((2, RealtimeWorldActorAggression.Aggressive), (feralWolf.Level, feralWolf.Aggression));
        Assert.Equal((5, RealtimeWorldActorAggression.Aggressive), (alpha.Level, alpha.Aggression));
        Assert.Equal((1, RealtimeWorldActorAggression.Defensive), (greyWolf.Level, greyWolf.Aggression));
        Assert.Equal((0, RealtimeWorldActorAggression.None), (mira.Level, mira.Aggression));
        Assert.Equal((0, RealtimeWorldActorAggression.None), (vein.Level, vein.Aggression));

        var dead = actors.First(value => value.Definition.Id == "mob.grey_wolf");
        Assert.True(store.TryDespawn(dead.NetworkEntityId, out _));
        var respawned = WorldActorProtocolMapper.ToSpawn(store.Respawn(dead));
        Assert.Equal((1, RealtimeWorldActorAggression.Defensive), (respawned.Level, respawned.Aggression));
        WorldActorProtocolCompatibility.Validate(actors);
    }

    [Fact]
    public void OnePeerCannotOverlapNpcAndCorpseButPeersCanShareNpc()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = actorStore.ActivateAssignment(
                "development-world-1",
                "local-shard-1",
                "test-runtime")
            .First();
        var leases = new WorldInteractionLeaseRegistry();

        Assert.True(leases.TryAcquireWorldActor(1, actor, out var first, out _, out _));
        Assert.True(leases.TryAcquireWorldActor(2, actor, out var second, out _, out _));
        Assert.NotEqual(first!.InteractionSessionId, second!.InteractionSessionId);
        Assert.False(leases.TryAcquireCorpse(1, Guid.NewGuid(), out _, out var code, out _));
        Assert.Equal("corpse_interaction_active", code);

        Assert.True(leases.ReleaseWorldActor(1, first.InteractionSessionId));
        var corpseId = Guid.NewGuid();
        Assert.True(leases.TryAcquireCorpse(1, corpseId, out var created, out _, out _));
        Assert.True(created);
        Assert.False(leases.TryAcquireWorldActor(1, actor, out _, out code, out _));
        Assert.Equal("world_interaction_active", code);
    }

    [Fact]
    public void InteractionUsesStartAndMaintainBoundsRanges()
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var atStartRange = CreatePlayer(harness.Session, -0.9f, -1f);
        var open = RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            harness.Session.SimulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            harness.Actor.NetworkEntityId,
            harness.Actor.RuntimeActorId,
            harness.Actor.InteractionRevision);

        var opened = harness.Authority.Process(1, atStartRange, open);

        Assert.NotNull(opened.Opened);
        var withinMaintainRange = CreatePlayer(harness.Session, -1.4f, -1f);
        var capability = opened.Opened!.Capabilities.Single(value =>
            value.Id == "services.dialogue");
        var action = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            harness.Session.SimulationSessionId,
            opened.Opened.InteractionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            harness.Actor.NetworkEntityId,
            harness.Actor.RuntimeActorId,
            harness.Actor.InteractionRevision,
            capability.Id,
            capability.Kind,
            capability.Revision);
        var maintained = harness.Authority.Process(1, withinMaintainRange, action);
        Assert.Equal("world_interaction_capability_deferred", maintained.Result!.Error.Code);
        Assert.Null(maintained.Closed);

        var outsideMaintainRange = CreatePlayer(harness.Session, -1.41f, -1f);
        var closed = harness.Authority.Process(1, outsideMaintainRange, action);
        Assert.Equal("world_interaction_out_of_range", closed.Result!.Error.Code);
        Assert.NotNull(closed.Closed);
    }

    [Fact]
    public void InteractionRejectsOutsideStartRangeAndAuthoritativeBlockedLineOfSight()
    {
        var clearHarness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var outside = CreatePlayer(clearHarness.Session, -0.91f, -1f);
        var outsideResult = clearHarness.Authority.Process(
            1,
            outside,
            CreateOpenIntent(clearHarness));

        Assert.Equal("world_interaction_out_of_range", outsideResult.Result!.Error.Code);

        var blocker = new CollisionBox(
            999,
            CollisionLayers.CombatQueries,
            new SimulationVector3(1.25f, 1.3f, -1f),
            new SimulationVector3(0.1f, 1f, 1f),
            SimulationQuaternion.Identity);
        var blockedWorld = new DynamicCollisionWorld(32f);
        blockedWorld.Upsert(blocker);
        var blockedHarness = CreateAuthorityHarness(blockedWorld);
        var blocked = blockedHarness.Authority.Process(
            1,
            CreatePlayer(blockedHarness.Session, 0f, -1f),
            CreateOpenIntent(blockedHarness));

        Assert.Equal(
            "world_interaction_line_of_sight_blocked",
            blocked.Result!.Error.Code);
    }

    [Fact]
    public void CapabilityKindAndRevisionAreServerFenced()
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var player = CreatePlayer(harness.Session, 0f, -1f);
        var opened = harness.Authority.Process(1, player, CreateOpenIntent(harness)).Opened;
        Assert.NotNull(opened);
        var capability = opened.Capabilities.First();
        var stale = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            harness.Session.SimulationSessionId,
            opened.InteractionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            harness.Actor.NetworkEntityId,
            harness.Actor.RuntimeActorId,
            harness.Actor.InteractionRevision,
            capability.Id,
            capability.Kind,
            capability.Revision + 1);

        var result = harness.Authority.Process(1, player, stale);

        Assert.Equal(
            "world_interaction_capability_unavailable",
            result.Result!.Error.Code);
        Assert.True(harness.Leases.TryGet(1, out _));
    }

    [Theory]
    [InlineData("stale", "world_interaction_target_changed")]
    [InlineData("inactive", "world_actor_unavailable")]
    [InlineData("session", "world_interaction_session_invalid")]
    [InlineData("missing", "world_actor_not_found")]
    [InlineData("assignment", "world_interaction_session_invalid")]
    [InlineData("worker_runtime", "world_interaction_session_invalid")]
    public void InteractionRejectsInvalidAuthorityState(string failure, string expectedCode)
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var player = CreatePlayer(harness.Session, 0f, -1f);
        var intent = CreateOpenIntent(harness);
        if (failure == "stale")
        {
            intent = RealtimeWorldInteractionIntent.CreateOpen(
                intent.OperationId,
                intent.SimulationSessionId,
                intent.TargetKind,
                intent.TargetEntityId,
                intent.TargetRuntimeId,
                intent.ExpectedTargetRevision + 1);
        }
        else if (failure == "inactive")
        {
            harness.Actor.SetActive(false);
            intent = CreateOpenIntent(harness);
        }
        else if (failure == "session")
        {
            intent = RealtimeWorldInteractionIntent.CreateOpen(
                intent.OperationId,
                Guid.NewGuid(),
                intent.TargetKind,
                intent.TargetEntityId,
                intent.TargetRuntimeId,
                intent.ExpectedTargetRevision);
        }
        else if (failure == "missing")
        {
            intent = RealtimeWorldInteractionIntent.CreateOpen(
                intent.OperationId,
                intent.SimulationSessionId,
                intent.TargetKind,
                ulong.MaxValue,
                Guid.NewGuid(),
                intent.ExpectedTargetRevision);
        }
        else if (failure == "assignment")
        {
            var wrongAssignment = harness.Session with { ShardId = "wrong-shard" };
            harness.SessionStore.Remove(
                harness.Session.CharacterId,
                harness.Session.SimulationSessionId,
                harness.Session.SimulationSessionToken);
            harness.SessionStore.Register(wrongAssignment);
            player = CreatePlayer(wrongAssignment, 0f, -1f);
        }
        else
        {
            var wrongAssignment = harness.Session with { WorkerRuntimeId = "wrong-runtime" };
            harness.SessionStore.Remove(
                harness.Session.CharacterId,
                harness.Session.SimulationSessionId,
                harness.Session.SimulationSessionToken);
            harness.SessionStore.Register(wrongAssignment);
            player = CreatePlayer(wrongAssignment, 0f, -1f);
        }

        var result = harness.Authority.Process(1, player, intent);

        Assert.Equal(expectedCode, result.Result!.Error.Code);
    }

    [Fact]
    public void RevalidationClosesOnlyAffectedLease()
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var firstPlayer = CreatePlayer(harness.Session, 0f, -1f);
        var secondSession = CreateSession();
        harness.SessionStore.Register(secondSession);
        var secondPlayer = CreatePlayer(secondSession, 0f, -1f);
        Assert.NotNull(harness.Authority.Process(1, firstPlayer, CreateOpenIntent(harness)).Opened);
        var secondOpen = RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            secondSession.SimulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            harness.Actor.NetworkEntityId,
            harness.Actor.RuntimeActorId,
            harness.Actor.InteractionRevision);
        Assert.NotNull(harness.Authority.Process(2, secondPlayer, secondOpen).Opened);

        var farFirstPlayer = CreatePlayer(harness.Session, -2f, -1f);
        var closures = harness.Authority.Revalidate(peerId =>
            peerId == 1 ? farFirstPlayer : secondPlayer);

        var closure = Assert.Single(closures);
        Assert.Equal(1, closure.PeerId);
        Assert.True(harness.Leases.TryGet(2, out _));
    }

    [Theory]
    [InlineData("despawn", "world_actor_not_found")]
    [InlineData("revision", "world_interaction_target_changed")]
    [InlineData("assignment", "world_interaction_session_invalid")]
    public void RevalidationClosesDespawnedRevisionChangedOrReassignedTargets(
        string change,
        string expectedCode)
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var player = CreatePlayer(harness.Session, 0f, -1f);
        Assert.NotNull(harness.Authority.Process(1, player, CreateOpenIntent(harness)).Opened);
        if (change == "despawn")
        {
            Assert.True(harness.Store.TryDespawn(harness.Actor.NetworkEntityId, out _));
        }
        else if (change == "revision")
        {
            harness.Actor.InvalidateInteractionRevision();
        }
        else
        {
            var reassigned = harness.Session with { ShardId = "replacement-shard" };
            harness.SessionStore.Remove(
                harness.Session.CharacterId,
                harness.Session.SimulationSessionId,
                harness.Session.SimulationSessionToken);
            harness.SessionStore.Register(reassigned);
            player = CreatePlayer(reassigned, 0f, -1f);
        }

        var closures = harness.Authority.Revalidate(_ => player);

        Assert.Equal(expectedCode, Assert.Single(closures).Closed.Code);
        Assert.False(harness.Leases.TryGet(1, out _));
    }

    [Fact]
    public void DisconnectRemovesOnlyThatPlayersInteractionLease()
    {
        var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var player = CreatePlayer(harness.Session, 0f, -1f);
        Assert.NotNull(harness.Authority.Process(1, player, CreateOpenIntent(harness)).Opened);

        Assert.True(harness.Leases.RemovePeer(1));

        Assert.False(harness.Leases.TryGet(1, out _));
    }

    [Fact]
    public void MetricsExposeOnlyBoundedPopulationAndOutcomeCounters()
    {
        using var metrics = new WorldActorMetrics();
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "test-runtime");

        metrics.ObservePopulation(actors);
        metrics.SetActiveInteractions(2);
        metrics.RecordInteraction(TimeSpan.FromMilliseconds(3), true);
        metrics.RecordInteraction(TimeSpan.FromMilliseconds(4), false);
        var snapshot = metrics.Capture();

        Assert.Equal(3, snapshot.NpcPopulation);
        Assert.Equal(2, snapshot.MobPopulation);
        Assert.Equal(2, snapshot.ActiveInteractions);
        Assert.Equal(1, snapshot.AcceptedInteractions);
        Assert.Equal(1, snapshot.RejectedInteractions);
    }

    [Fact]
    public void LargeEventDrivenPopulationAndMobTransitionsStayCentrallyScheduled()
    {
        const int population = 4_000;
        var runtime = WorldActorTestData.Compile();
        runtime.SpawnInstances = Enumerable.Range(0, population)
            .Select(index => new WorldActorSpawnInstanceDefinition
            {
                Id = $"phase15.load.{index:D5}",
                SpawnDefinitionId = "phase15.load",
                ActorId = index % 2 == 0 ? "npc.city_services" : "mob.feral_wolf",
                X = (index % 20) * 0.25f,
                Y = 0f,
                Z = ((index / 20) % 20) * 0.25f,
                YawDegrees = 0f,
                PatrolPathId = string.Empty,
                StructuralFingerprint = $"phase15-{index:D5}"
            })
            .ToArray();
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(runtime, registry);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            "test-runtime");
        var scheduler = new WorldActorActivityScheduler(actorStore);
        var player = RegisterPlayer(registry, CreateSession(), 0f, -1f);

        var activated = scheduler.Evaluate([player], 60, 30);
        var returnedDormant = scheduler.Evaluate([], 66, 30);

        Assert.Equal(population, actors.Count);
        Assert.Equal(population / 2, activated.Count);
        Assert.Equal(population / 2, returnedDormant.Count);
        Assert.All(
            actors.Where(actor => actor.Definition.Kind == WorldActorKindIds.Npc),
            actor => Assert.Equal(WorldActorActivityTierIds.EventDriven, actor.ActivityTier));
        Assert.All(
            actors.Where(actor => actor.Definition.Kind == WorldActorKindIds.Mob),
            actor => Assert.Equal(WorldActorActivityTierIds.Dormant, actor.ActivityTier));
    }

    [Fact]
    public void PlayerGridFindsTheNearestAcceptedPlayerAcrossCells()
    {
        var registry = new SimulationEntityRegistry();
        var far = RegisterPlayer(registry, CreateSession(), 40f, 0f);
        var near = RegisterPlayer(registry, CreateSession(), 17f, 0f);
        var nearest = RegisterPlayer(registry, CreateSession(), -3f, 4f);
        var grid = new PlayerSpatialGrid();
        grid.Rebuild([far, near, nearest]);

        Assert.Equal(3, grid.Count);
        Assert.Same(nearest, grid.FindNearest(0f, 0f, 20f, null));
        Assert.Same(near, grid.FindNearest(0f, 0f, 20f, player => player != nearest));
        Assert.Null(grid.FindNearest(0f, 0f, 4.9f, null));
        Assert.True(grid.AnyWithin(39f, 0f, 1f));
        Assert.False(grid.AnyWithin(0f, 30f, 10f));
    }

    [Fact]
    public void PlayerGridBreaksDistanceTiesByEntityId()
    {
        var registry = new SimulationEntityRegistry();
        var first = RegisterPlayer(registry, CreateSession(), 5f, 0f);
        var second = RegisterPlayer(registry, CreateSession(), -5f, 0f);
        var grid = new PlayerSpatialGrid();
        grid.Rebuild([second, first]);

        var lower = first.NetworkEntityId < second.NetworkEntityId ? first : second;
        Assert.Same(lower, grid.FindNearest(0f, 0f, 10f, null));
    }

    [Fact]
    public void ABusyMobStaysActiveWithoutPlayersUntilItIsIdleAgain()
    {
        var registry = new SimulationEntityRegistry();
        var actorStore = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actors = actorStore.ActivateAssignment("development-world-1", "local-shard-1", "test-runtime");
        var mob = actors.First(actor => actor.Definition.Kind == WorldActorKindIds.Mob);
        var scheduler = new WorldActorActivityScheduler(actorStore);
        var player = RegisterPlayer(registry, CreateSession(), mob.PositionX, mob.PositionZ);
        var busy = true;

        Assert.Contains(mob, scheduler.Evaluate(PlayerSpatialGrid.From([player]), 60, 30, _ => busy));
        Assert.DoesNotContain(mob, scheduler.Evaluate(new PlayerSpatialGrid(), 66, 30, _ => busy));
        Assert.Equal(WorldActorActivityTierIds.Active, mob.ActivityTier);

        busy = false;
        Assert.Contains(mob, scheduler.Evaluate(new PlayerSpatialGrid(), 69, 30, _ => busy));
        Assert.Equal(WorldActorActivityTierIds.Dormant, mob.ActivityTier);
    }

    [Fact]
    public async Task ConcurrentNpcInteractionHotspotKeepsIndependentPlayerLeases()
    {
        const int playerCount = 128;
        using var harness = CreateAuthorityHarness(new EmptyCollisionWorld());
        var requests = new List<(int PeerId, PlayerSimulationEntity Player,
            RealtimeWorldInteractionIntent Intent)>(playerCount);
        for (var index = 0; index < playerCount; index++)
        {
            var session = index == 0 ? harness.Session : CreateSession();
            if (index > 0)
            {
                Assert.Equal(
                    ActiveSimulationSessionRegistration.Joined,
                    harness.SessionStore.Register(session));
            }

            requests.Add((
                index + 1,
                CreatePlayer(session, 0f, -1f),
                RealtimeWorldInteractionIntent.CreateOpen(
                    Guid.NewGuid(),
                    session.SimulationSessionId,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    harness.Actor.NetworkEntityId,
                    harness.Actor.RuntimeActorId,
                    harness.Actor.InteractionRevision)));
        }

        var results = await Task.WhenAll(requests.Select(request =>
            harness.Authority.ProcessAsync(
                request.PeerId,
                request.Player,
                request.Intent,
                CancellationToken.None)));

        Assert.All(results, result => Assert.NotNull(result.Opened));
        Assert.Equal(playerCount, harness.Leases.ListWorldActorLeases().Count);
        var metrics = harness.Metrics.Capture();
        Assert.Equal(playerCount, metrics.AcceptedInteractions);
        Assert.Equal(playerCount, metrics.ActiveInteractions);
    }

    [Fact]
    public void WorldInteractionIntentBurstIsBoundedWithStableRejectionCode()
    {
        var limiter = new WorldInteractionIntentLimiter();

        for (var index = 0; index < (int)WorldInteractionIntentLimiter.BurstCapacity; index++)
        {
            Assert.True(limiter.TryConsume());
        }

        Assert.False(limiter.TryConsume());
        Assert.Equal(
            "world_interaction_rate_limited",
            WorldInteractionIntentLimiter.RejectionCode);
    }

    private static AuthorityHarness CreateAuthorityHarness(ICollisionWorld collisionWorld)
    {
        var config = CreateConfig();
        var registry = new SimulationEntityRegistry();
        var store = new WorldActorStore(WorldActorTestData.Compile(), registry);
        var actor = store.ActivateAssignment(
                config.WorldId,
                config.ShardId,
                "test-runtime")
            .Single(value => value.Definition.Id == "npc.city_services");
        var sessionStore = new ActiveSimulationSessionStore();
        var session = CreateSession();
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, sessionStore.Register(session));
        var leases = new WorldInteractionLeaseRegistry();
        var metrics = new WorldActorMetrics();
        var authority = new WorldInteractionAuthorityService(
            config,
            sessionStore,
            store,
            leases,
            new WorldActorCapabilityRegistry(
                new DefaultWorldActorCapabilityAvailabilityPolicy()),
            new WorldActorLineOfSightService(collisionWorld),
            metrics);
        return new AuthorityHarness(
            authority,
            store,
            actor,
            session,
            sessionStore,
            leases,
            metrics);
    }

    private static RealtimeWorldInteractionIntent CreateOpenIntent(AuthorityHarness harness)
    {
        return RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            harness.Session.SimulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            harness.Actor.NetworkEntityId,
            harness.Actor.RuntimeActorId,
            harness.Actor.InteractionRevision);
    }

    private static PlayerSimulationEntity CreatePlayer(
        ActiveSimulationSession session,
        float x,
        float z)
    {
        var settings = CreateMovementSettings();
        var collisionWorld = new EmptyCollisionWorld();
        var initial = PlayerMovementSimulation.CreateInitialState(
            settings,
            collisionWorld,
            x,
            0f,
            z,
            0f);
        return new PlayerSimulationEntity(
            9999,
            session,
            new AuthoritativePlayerMovement(
                initial,
                settings,
                session.CarryState,
                collisionWorld));
    }

    private static PlayerSimulationEntity RegisterPlayer(
        SimulationEntityRegistry registry,
        ActiveSimulationSession session,
        float x,
        float z)
    {
        var player = CreatePlayer(session, x, z);
        return registry.RegisterPlayer(session, () => player.Movement).Entity;
    }

    private static ActiveSimulationSession CreateSession()
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            "test-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "World Actor Tester",
            "local-shard-1",
            "development-world-1",
            "local-simulation-worker-1",
            "test-runtime",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            new PlayerCarryState(1, 0, 200),
            false);
    }

    private static SimulationWorkerConfig CreateConfig()
    {
        return new SimulationWorkerConfig(
            "local-simulation-worker-1",
            "local-fleet",
            "local-node-1",
            "local-shard-1",
            "development-world-1",
            "CollisionData",
            27015,
            "127.0.0.1",
            27015,
            10,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(5),
            15,
            CreateMovementSettings(),
            new MovementSpawnConfig(0f, 0f, -1f, 0f),
            new Uri("http://game-service.test"),
            TimeSpan.FromSeconds(2),
            "test-simulation-worker-secret-at-least-32-characters",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1));
    }

    private static MovementSimulationSettings CreateMovementSettings()
    {
        return new MovementSimulationSettings(
            30,
            5f,
            720f,
            -24f,
            -2f,
            0f,
            -100f,
            100f,
            -100f,
            100f);
    }

    private sealed record AuthorityHarness(
        WorldInteractionAuthorityService Authority,
        WorldActorStore Store,
        WorldActorRuntimeState Actor,
        ActiveSimulationSession Session,
        ActiveSimulationSessionStore SessionStore,
        WorldInteractionLeaseRegistry Leases,
        WorldActorMetrics Metrics) : IDisposable
    {
        public void Dispose()
        {
            Metrics.Dispose();
        }
    }

    private sealed class CharacterFilteredCapabilityPolicy(Guid allowedCharacterId)
        : IWorldActorCapabilityAvailabilityPolicy
    {
        public bool IsAvailable(
            ActiveSimulationSession session,
            WorldActorRuntimeState actor,
            WorldActorCapabilityDefinition capability)
        {
            return session.CharacterId == allowedCharacterId;
        }
    }

    private sealed class TestVendorCapabilityHandler : IWorldActorCapabilityHandler
    {
        public RealtimeWorldActorCapabilityKind Kind =>
            RealtimeWorldActorCapabilityKind.Vendor;

        public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
            WorldActorCapabilityOperationContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(WorldActorCapabilityKindIds.Vendor, context.Capability.Kind);
            return Task.FromResult(
                new WorldActorCapabilityDispatchResult(true, string.Empty, string.Empty));
        }
    }

    private static RealtimeWorldInteractionIntent CreateCapabilityIntent(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor,
        WorldActorCapabilityDefinition capability)
    {
        var summary = new WorldActorCapabilityRegistry(
                new DefaultWorldActorCapabilityAvailabilityPolicy())
            .BuildSummary(session, actor);
        var protocolCapability = summary.Capabilities.Single(value =>
            string.Equals(value.Id, capability.Id, StringComparison.Ordinal));
        return RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            session.SimulationSessionId,
            Guid.NewGuid(),
            RealtimeWorldInteractionTargetKind.WorldActor,
            actor.NetworkEntityId,
            actor.RuntimeActorId,
            actor.InteractionRevision,
            protocolCapability.Id,
            protocolCapability.Kind,
            protocolCapability.Revision);
    }

    private sealed class EmptyCollisionWorld : ICollisionWorld
    {
        public void QueryShapes(
            CollisionAabb bounds,
            uint layerMask,
            CollisionQueryBuffer buffer)
        {
            buffer.Clear();
        }
    }

}
