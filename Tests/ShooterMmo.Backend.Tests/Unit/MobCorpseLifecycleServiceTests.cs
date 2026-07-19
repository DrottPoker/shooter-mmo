using System.Net;
using Microsoft.Extensions.Configuration;
using SimulationWorker.Auth;
using SimulationWorker.Config;
using SimulationWorker.Corpses;
using SimulationWorker.Entities;
using SimulationWorker.Registry;
using SimulationWorker.WorldActors;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class MobCorpseLifecycleServiceTests
{
    [Fact]
    public async Task LiveMobCreationUsesDefinitionLifetimeAndDeterministicGrantIds()
    {
        var runtime = WorldActorTestData.Compile();
        var runtimeIds = Enumerable.Range(1, runtime.SpawnInstances.Length)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var runtimeIndex = 0;
        var actorStore = new WorldActorStore(
            runtime,
            new SimulationEntityRegistry(),
            () => runtimeIds[runtimeIndex++]);
        var identity = new SimulationWorkerIdentity("phase14-runtime", DateTime.UtcNow);
        var actors = actorStore.ActivateAssignment(
            "development-world-1",
            "local-shard-1",
            identity.RuntimeId);
        var wolf = actors.First(actor => actor.Definition.Id == "mob.feral_wolf");
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        var liveStore = new LiveMobCorpseStore(timeProvider);
        var service = new MobCorpseLifecycleService(
            actorStore,
            liveStore,
            new DurableCorpseStore(timeProvider),
            new AuthServiceClient(new HttpClient(new UnexpectedHttpHandler())
            {
                BaseAddress = new Uri("http://localhost")
            }),
            LoadConfig(),
            identity,
            timeProvider);
        var deathEventId = Guid.NewGuid();
        var request = new CreateMobCorpseRequest(
            deathEventId,
            wolf.RuntimeActorId,
            [new MobCorpseLootSeed("material.iron_ore", 2)]);

        var first = await service.CreateAsync(request, CancellationToken.None);
        var replay = await service.CreateAsync(request, CancellationToken.None);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.NotNull(first.LiveCorpse);
        Assert.Null(first.DurableCorpse);
        Assert.Equal(TimeSpan.FromSeconds(120),
            first.LiveCorpse.ExpiresAt - first.LiveCorpse.CreatedAt);
        Assert.Equal(first.LiveCorpse.CorpseId, replay.LiveCorpse!.CorpseId);
        Assert.Equal(
            Assert.Single(first.LiveCorpse.Loot).GrantId,
            Assert.Single(replay.LiveCorpse.Loot).GrantId);
        Assert.Equal(first.LiveCorpse, Assert.Single(liveStore.ListActive()));
    }

    private static SimulationWorkerConfig LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindRepositoryRoot())
            .AddJsonFile("SimulationWorker/Config/appsettings.json")
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SIMULATION_WORKER_SERVICE_SECRET"] =
                    "phase-fourteen-unit-test-secret-at-least-32-characters",
                ["ConnectionStrings:Redis"] = "localhost:6379"
            })
            .Build();
        return SimulationWorkerConfig.FromConfiguration(configuration);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ShooterMmo.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Shooter MMO repository root.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
