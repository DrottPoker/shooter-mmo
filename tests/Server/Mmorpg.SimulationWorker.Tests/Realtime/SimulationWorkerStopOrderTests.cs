using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.WorldData.Items;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Runs the worker's hosted services in a generic host, registered as the worker registers
// them, against the scripted GameService, and pins the order of the GameService calls that
// stopping the host makes.
public sealed class SimulationWorkerStopOrderTests
{
    [Fact]
    public async Task AStoppingWorkerCommitsItsShotsBeforeItGoesOfflineAndReleasesItsSessions()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        var itemCatalog = TestCatalog(minimumDamage: 1, maximumDamage: 1);
        var combatRules = TestRules(batchIntervalSeconds: 60f);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = itemCatalog,
            CombatRules = combatRules,
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll(),
            RegistrationLease = lease
        });
        var archer = harness.AddCharacter("Closing Time Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Closing Time Target");
        using var host = BuildHost(harness, lease, itemCatalog, combatRules);
        using var timeout = RealtimeServerHarness.CreateTimeout();

        await host.StartAsync(timeout.Token);
        try
        {
            await harness.PollUntilAsync(() => lease.IsValid, "the worker to register");
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
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        // The arrow batch commits while both sessions and the registration still hold; then the
        // worker heartbeat marks the runtime offline and the session heartbeat releases both
        // sessions.
        Assert.Equal(
            ["ammunition-consumptions", "offline", "release", "release"],
            harness.GameService.Calls
                .Select(path => path[(path.LastIndexOf('/') + 1)..])
                .Where(call => call is "ammunition-consumptions" or "offline" or "release"));
        Assert.True(Assert.Single(harness.GameService.AmmunitionConsumptions).Quantity >= 1);
    }

    // A tick phase that fails every run stops the worker after the limit the way an unhandled
    // exception does: the realtime shutdown saves the remaining player in one last checkpoint,
    // and then the worker goes offline and releases the session.
    [Fact]
    public async Task ATickPhaseThatKeepsFailingStopsTheWorkerInOrder()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        var itemCatalog = TestCatalog();
        var combatRules = TestRules();
        // The one collision chunk whose file is missing lies three 4 m chunks east of the
        // player's saved place, one step beyond the streaming radius, so the collision streaming
        // phase fails every tick once the player stepped east.
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            ItemCatalog = itemCatalog,
            CombatRules = combatRules,
            RegistrationLease = lease,
            CollisionStreamingStore = RealtimeLoopFailureTests.BrokenStreamingStore(chunkSize: 4f, chunkX: 3, chunkZ: -1),
            TickPhaseFailureLimit = 5,
            ExpectLoopFailures = true
        });
        var stranded = harness.AddCharacter("Stranded Walker") with
        {
            SavedState = new SimulationSavedCharacterState(
                RealtimeServerHarness.WorldId,
                harness.StaticCollisionWorld.Revision,
                3.9f,
                0f,
                -1f,
                90f,
                40,
                10)
        };
        harness.GameService.AddCharacter(stranded);
        using var host = BuildHost(harness, lease, itemCatalog, combatRules);
        using var timeout = RealtimeServerHarness.CreateTimeout();

        await host.StartAsync(timeout.Token);
        await harness.PollUntilAsync(() => lease.IsValid, "the worker to register");
        var walker = await harness.JoinAsync(stranded);
        walker.SendMoveTo(1, 10f, -1f);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await harness.PollUntilAsync(() => lifetime.ApplicationStopping.IsCancellationRequested, "the worker to stop itself");
        await host.WaitForShutdownAsync(timeout.Token);

        Assert.Equal(5, harness.LoopFailures);
        var entries = harness.Logger.Entries;
        Assert.Equal(5, entries.Count(entry => entry.Level == LogLevel.Error));
        var critical = entries.Single(entry => entry.Level == LogLevel.Critical);
        Assert.Contains("tick phase collision streaming failed 5 runs in a row", critical.Message, StringComparison.Ordinal);
        Assert.True(
            entries.ToList().FindIndex(entry => entry.Message == "[SIMULATION] Realtime transport stopped.")
            > entries.ToList().IndexOf(critical));
        Assert.Equal(
            ["consume", "checkpoint", "offline", "release"],
            harness.GameService.Calls
                .Select(path => path[(path.LastIndexOf('/') + 1)..])
                .Where(call => call is "consume" or "checkpoint" or "offline" or "release"));
        var saved = Assert.Single(Assert.Single(harness.GameService.Checkpoints).States);
        Assert.True(saved.PositionX >= 4f);
    }

    // The worker's hosted services with the harness's collaborators; the realtime service is
    // the harness's own, composed like production, in the place the worker registers it.
    internal static IHost BuildHost(
        RealtimeServerHarness harness,
        SimulationWorkerRegistrationLease lease,
        ItemCatalogRuntimeDocument itemCatalog,
        CombatRules combatRules)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var services = builder.Services;
        // No session heartbeat runs during the test, so only the stop releases sessions.
        services.AddSingleton(harness.Config with { SimulationSessionHeartbeatInterval = TimeSpan.FromHours(1) });
        services.AddSingleton(harness.GameServiceClient);
        services.AddSingleton(harness.ReleaseService);
        services.AddSingleton(harness.SessionStore);
        services.AddSingleton(harness.CharacterItemStateStore);
        services.AddSingleton(harness.CorpseStore);
        services.AddSingleton(harness.Identity);
        services.AddSingleton(harness.Readiness);
        services.AddSingleton(harness.StaticCollisionWorld);
        services.AddSingleton(itemCatalog);
        services.AddSingleton(RealtimeServerHarness.QuestCatalog);
        services.AddSingleton(combatRules);
        services.AddSingleton(harness.ShardRules);
        services.AddSingleton(lease);
        services.AddSingleton(harness.NetworkMetrics!);
        services.AddSingleton(harness.PerformanceMetrics!);
        services.AddSimulationWorkerHostedServices();
        var realtime = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(RealtimeSimulationService));
        services[services.IndexOf(realtime)] = ServiceDescriptor.Singleton<IHostedService>(harness.Service);
        return builder.Build();
    }
}
