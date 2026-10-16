using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Characterizes world actor replication and world interaction authority over UDP. The
// player spawns at (0, 0, -1), next to the city services NPC at (2.5, 0, -1).
public sealed class RealtimeWorldActorInteractionTests
{
    private const string ServicesActorId = "npc.city_services";

    [Fact]
    public async Task WorldActorSpawnsArriveInTheJoinBaseline()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true
        });
        var character = harness.AddCharacter("Actor Watcher");
        await harness.StartAsync();

        var client = await harness.JoinAsync(character);
        await harness.PollUntilAsync(
            () => client.WorldActorSpawns.Any(spawn => spawn.ActorDefinitionId == ServicesActorId),
            "the world actor baseline");

        Assert.Equal(RealtimeMessageType.JoinAccepted, client.ReliablePackets[0].Type);
        var services = client.WorldActorSpawns.Single(spawn =>
            spawn.ActorDefinitionId == ServicesActorId);
        Assert.Equal(2.5f, services.PositionX);
        Assert.Equal(harness.WorldActors!.Count, client.WorldActorSpawns.Count);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Template == "[SIMULATION] Activated {ActorCount} world actors from revision {WorldActorRevision} for worker runtime {RuntimeId}.");
        Assert.Equal(
            harness.WorldActors.Count + 1,
            harness.NetworkMetrics!.Capture().ActiveEntities);
    }

    [Fact]
    public async Task OpenThenCloseSendsOpenedResultAndClosedInOrder()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true
        });
        var character = harness.AddCharacter("Actor Visitor");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var services = await WaitForServicesAsync(harness, client);

        client.SendWorldIntent(CreateOpen(character.SimulationSessionId, services));
        await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");
        var opened = client.WorldOpened[0];
        Assert.Equal("Mira the Quartermaster", opened.DisplayName);

        var close = RealtimeWorldInteractionIntent.CreateClose(
            Guid.NewGuid(),
            character.SimulationSessionId,
            opened.InteractionSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            services.EntityId,
            services.RuntimeActorId,
            services.InteractionRevision);
        client.SendWorldIntent(close);
        await harness.PollUntilAsync(() => client.WorldClosures.Count == 1, "the close");

        var result = Assert.Single(client.WorldResults);
        Assert.Equal(close.OperationId, result.OperationId);
        Assert.True(result.Succeeded);
        var closed = Assert.Single(client.WorldClosures);
        Assert.Equal("world_interaction_closed", closed.Code);
        var openedIndex = client.IndexOfFirst(RealtimeMessageType.WorldInteractionOpened);
        var resultIndex = client.IndexOfFirst(RealtimeMessageType.WorldInteractionResult);
        var closedIndex = client.IndexOfFirst(RealtimeMessageType.WorldInteractionClosed);
        Assert.True(openedIndex < resultIndex);
        Assert.True(resultIndex < closedIndex);
        Assert.False(harness.WorldInteractionLeases!.TryGet(0, out _));
    }

    [Fact]
    public async Task IntentBurstIsRateLimitedBeforeTheQueue()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true
        });
        var character = harness.AddCharacter("Burst Visitor");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var intents = Enumerable.Range(0, 20)
            .Select(_ => RealtimeWorldInteractionIntent.CreateOpen(
                Guid.NewGuid(),
                character.SimulationSessionId,
                RealtimeWorldInteractionTargetKind.WorldActor,
                999_999,
                Guid.NewGuid(),
                1))
            .ToArray();

        foreach (var intent in intents)
        {
            client.SendWorldIntent(intent);
        }

        await harness.PollUntilAsync(
            () => client.WorldResults.Count == intents.Length,
            "a result for every intent");

        var results = client.WorldResults.ToDictionary(result => result.OperationId);
        var limited = intents
            .Select((intent, index) => (Index: index, Result: results[intent.OperationId]))
            .Where(value => value.Result.Error?.Code == WorldInteractionIntentLimiter.RejectionCode)
            .ToArray();
        Assert.NotEmpty(limited);
        Assert.All(limited, value =>
        {
            Assert.True(value.Index >= 12, "The burst capacity admits the first twelve intents.");
            Assert.Equal(
                "World interaction intents exceeded the per-peer rate limit.",
                value.Result.Error.Message);
        });
        Assert.All(
            results.Values.Where(result =>
                result.Error?.Code != WorldInteractionIntentLimiter.RejectionCode),
            result => Assert.Contains(
                result.Error.Code,
                new[] { "world_actor_not_found", "world_interaction_queue_full" }));
        Assert.Null(client.ServerDisconnect);
    }

    [Fact]
    public async Task QueuedWorldInteractionsWaitBehindAPendingCapability()
    {
        var gate = new TestGate();
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            CapabilityHandlers = [new GatedDialogueHandler(gate)]
        });
        var character = harness.AddCharacter("Queued Visitor");
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            var services = await WaitForServicesAsync(harness, client);
            client.SendWorldIntent(CreateOpen(character.SimulationSessionId, services));
            await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");
            var opened = client.WorldOpened[0];
            var dialogue = opened.Capabilities.Single(value => value.Id == "services.dialogue");
            var actions = Enumerable.Range(0, 10)
                .Select(_ => RealtimeWorldInteractionIntent.CreateCapabilityAction(
                    Guid.NewGuid(),
                    character.SimulationSessionId,
                    opened.InteractionSessionId,
                    RealtimeWorldInteractionTargetKind.WorldActor,
                    services.EntityId,
                    services.RuntimeActorId,
                    services.InteractionRevision,
                    dialogue.Id,
                    dialogue.Kind,
                    dialogue.Revision))
                .ToArray();

            client.SendWorldIntent(actions[0]);
            await harness.PollUntilAsync(() => gate.HasEntered, "the capability to start");
            foreach (var action in actions.Skip(1))
            {
                client.SendWorldIntent(action);
            }

            await harness.PollUntilAsync(() => client.WorldResults.Count == 1, "the queue-full result");
            var rejection = Assert.Single(client.WorldResults);
            Assert.Equal(actions[9].OperationId, rejection.OperationId);
            Assert.Equal("world_interaction_queue_full", rejection.Error.Code);
            Assert.Equal(
                "Too many authority operations are waiting for this peer.",
                rejection.Error.Message);

            gate.Release();
            await harness.PollUntilAsync(
                () => client.WorldResults.Count == 10,
                "the queued capability results");
            Assert.Equal(
                actions.Take(9).Select(action => action.OperationId),
                client.WorldResults.Skip(1).Select(result => result.OperationId));
            Assert.All(client.WorldResults.Skip(1), result => Assert.True(result.Succeeded));
            Assert.Equal(9, gate.EntryCount);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task WalkingAwayClosesTheInteractionAndDespawnsTheActor()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            InterestManagement = new InterestManagementConfig(2f, 3f, 4f)
        });
        var character = harness.AddCharacter("Departing Visitor");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var services = await WaitForServicesAsync(harness, client);
        Assert.Single(client.WorldActorSpawns);
        client.SendWorldIntent(CreateOpen(character.SimulationSessionId, services));
        await harness.PollUntilAsync(() => client.WorldOpened.Count == 1, "the open");

        uint sequence = 0;
        using var timeout = RealtimeServerHarness.CreateTimeout();
        while (!(client.WorldClosures.Count == 1
            && client.WorldActorDespawns.Any(despawn => despawn.EntityId == services.EntityId)))
        {
            client.SendMoveTo(++sequence, -14f, -1f);
            await Task.Delay(50, timeout.Token);
            harness.PollClients();
        }

        var closed = Assert.Single(client.WorldClosures);
        Assert.Equal(client.WorldOpened[0].InteractionSessionId, closed.InteractionSessionId);
        Assert.Equal("world_interaction_out_of_range", closed.Code);
        Assert.Equal("Move closer to continue interacting.", closed.Message);
        var despawn = client.WorldActorDespawns.Single(value => value.EntityId == services.EntityId);
        Assert.Equal("out_of_interest", despawn.Reason);
        Assert.Equal(services.RuntimeActorId, despawn.RuntimeActorId);
    }

    [Fact]
    public async Task DespawnedWorldActorIsPrunedWhileNoPlayerIsBound()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true
        });
        await harness.StartAsync();
        var actor = harness.WorldActors!.ListActors()[0];

        Assert.True(harness.WorldActors.TryDespawn(actor.NetworkEntityId, out _));
        Assert.Equal(1, harness.WorldActors.DespawnedCount);

        await harness.PollUntilAsync(
            () => harness.WorldActors.DespawnedCount == 0,
            "the despawned actor to be pruned");
        Assert.False(harness.WorldActors.TryGetDespawnedByEntityId(actor.NetworkEntityId, out _));
    }

    internal static async Task<RealtimeWorldActorSpawn> WaitForServicesAsync(
        RealtimeServerHarness harness,
        RealtimeRecordingClient client)
    {
        await harness.PollUntilAsync(
            () => client.WorldActorSpawns.Any(spawn => spawn.ActorDefinitionId == ServicesActorId),
            "the city services spawn");
        return client.WorldActorSpawns.Single(spawn => spawn.ActorDefinitionId == ServicesActorId);
    }

    internal static RealtimeWorldInteractionIntent CreateOpen(
        Guid simulationSessionId,
        RealtimeWorldActorSpawn actor)
    {
        return RealtimeWorldInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            simulationSessionId,
            RealtimeWorldInteractionTargetKind.WorldActor,
            actor.EntityId,
            actor.RuntimeActorId,
            actor.InteractionRevision);
    }

    internal sealed class GatedDialogueHandler(TestGate gate) : IWorldActorCapabilityHandler
    {
        public RealtimeWorldActorCapabilityKind Kind => RealtimeWorldActorCapabilityKind.Dialogue;

        public async Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
            WorldActorCapabilityOperationContext context,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync();
            return new WorldActorCapabilityDispatchResult(true, string.Empty, "Hello.");
        }
    }
}
