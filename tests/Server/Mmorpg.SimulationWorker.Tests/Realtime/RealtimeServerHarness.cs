using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Characters;
using Mmorpg.SimulationWorker.Appearance;
using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Characters;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

internal sealed class RealtimeServerHarnessOptions
{
    public bool EnableWorldActors { get; init; }

    public bool EnableLiveMobCorpses { get; init; }

    public TimeSpan JoinHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public UdpQuotaConfig? UdpQuotas { get; init; }

    public InterestManagementConfig? InterestManagement { get; init; }

    public IReadOnlyList<IWorldActorCapabilityHandler> CapabilityHandlers { get; init; } = [];

    public SimulationWorkerRegistrationLease? RegistrationLease { get; init; }

    public IReadOnlyList<TimeSpan>? DetachedReleaseRetryDelays { get; init; }

    public bool ProvideMetrics { get; init; } = true;

    // Combat runs with these rules, items, and Shard rule; without them it uses the defaults.
    public CombatRules? CombatRules { get; init; }

    public ItemCatalogRuntimeDocument? ItemCatalog { get; init; }

    public bool PlayerVersusPlayer { get; init; }

    // Lets players use the development admin tools, as a Development worker does.
    public bool EnableAdminTools { get; init; }

    public ICombatRandom? CombatRandom { get; init; }

    // Replaces the compiled actor runtime of the development World.
    public WorldActorRuntimeDocument? WorldActorDocument { get; init; }

    // Mobs that die leave corpses through the real lifecycle service.
    public bool EnableMobCorpses { get; init; }
}

internal sealed record RealtimeTestCharacter(string Ticket, string Name)
{
    public Guid AccountId { get; init; } = Guid.NewGuid();

    public Guid CharacterId { get; init; } = Guid.NewGuid();

    public Guid SimulationSessionId { get; init; } = Guid.NewGuid();

    public string SimulationSessionToken { get; init; } = Guid.NewGuid().ToString("N");

    public bool IsSyntheticBot { get; init; }

    public long ItemStateRevision { get; init; }

    public long CarriedWeight { get; init; }

    public long CarryCapacity { get; init; } = 200;

    public long SessionLeaseDurationMilliseconds { get; init; } = 30_000;

    public IReadOnlyList<SimulationEquippedItem> Equipment { get; init; } = [];

    // Ignored for a development bot, which the worker dresses itself.
    public SimulationAppearance Appearance { get; init; } = CharacterStateTestData.Appearance;
}

// Owns one realtime service with every collaborator exposed, a scripted GameService and
// recording UDP clients. Each wait polls the clients and fails after ten seconds.
internal sealed class RealtimeServerHarness : IAsyncDisposable
{
    public const string WorkerId = "local-simulation-worker-1";
    public const string ShardId = "local-shard-1";
    public const string WorldId = "development-world-1";
    public const string RuntimeId = "test-runtime";
    public static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient httpClient;
    private readonly List<RealtimeRecordingClient> clients = [];
    private readonly List<IDisposable> disposables = [];
    private bool started;
    private bool stopped;

    private RealtimeServerHarness(RealtimeServerHarnessOptions options)
    {
        Options = options;
        Port = FindAvailableUdpPort();
        Config = CreateConfig(Port, options);
        GameService = new ScriptedGameServiceHandler();
        httpClient = new HttpClient(GameService)
        {
            BaseAddress = new Uri("http://game-service.test")
        };
        GameServiceClient = new GameServiceClient(httpClient);
        Identity = new SimulationWorkerIdentity(RuntimeId, DateTime.UtcNow.AddMinutes(-1));
        StaticCollisionWorld = CollisionTestWorldFactory.Create();
        CollisionWorld = new CompositeCollisionWorld(
            StaticCollisionWorld,
            new DynamicCollisionWorld(StaticCollisionWorld.ChunkSize));
        if (options.ProvideMetrics)
        {
            NetworkMetrics = Track(new RealtimeNetworkMetrics());
            PerformanceMetrics = Track(new RealtimePerformanceMetrics());
        }

        WorldActorDocument = options.WorldActorDocument ?? (options.EnableWorldActors ? WorldActorTestData.Compile() : null);
        if (options.EnableWorldActors)
        {
            WorldActors = new WorldActorStore(WorldActorDocument!, EntityRegistry);
            WorldActorScheduler = new WorldActorActivityScheduler(WorldActors);
            WorldInteractionLeases = new WorldInteractionLeaseRegistry();
            WorldActorMetrics = Track(new WorldActorMetrics());
            WorldInteractionAuthority = new WorldInteractionAuthorityService(
                Config,
                SessionStore,
                WorldActors,
                WorldInteractionLeases,
                new WorldActorCapabilityRegistry(
                    new DefaultWorldActorCapabilityAvailabilityPolicy(),
                    options.CapabilityHandlers),
                new WorldActorLineOfSightService(CollisionWorld),
                WorldActorMetrics);
        }

        if (options.EnableLiveMobCorpses || options.EnableMobCorpses)
        {
            LiveMobCorpseStore = new LiveMobCorpseStore(TimeProvider.System);
            LiveMobCorpseInteraction = new LiveMobCorpseInteractionService(
                LiveMobCorpseStore,
                GameServiceClient,
                CharacterItemStateStore);
            CorpseRuntimeMetrics = Track(new CorpseRuntimeMetrics());
        }

        ShardRules.Set(new ShardRuleSet("test", options.PlayerVersusPlayer));
        CombatMetrics = Track(new CombatMetrics());
        var mobCorpses = options.EnableMobCorpses && WorldActors is not null
            ? new MobCorpseLifecycleService(
                WorldActors,
                LiveMobCorpseStore!,
                CorpseStore,
                GameServiceClient,
                Config,
                Identity,
                TimeProvider.System)
            : null;

        Service = new RealtimeSimulationService(
            Config,
            new SimulationJoinService(
                GameServiceClient,
                SessionStore,
                CharacterItemStateStore,
                Config,
                Identity,
                Looks,
                new DevelopmentBotLooks(AppearanceCatalog, options.ItemCatalog ?? ItemCatalogTestData.CompileFixture())),
            new SimulationSessionReleaseService(
                GameServiceClient,
                SessionStore,
                CharacterItemStateStore,
                options.DetachedReleaseRetryDelays),
            SessionStore,
            CharacterItemStateStore,
            new SimulationItemInteractionService(GameServiceClient),
            new SimulationCorpseInteractionService(GameServiceClient),
            CorpseStore,
            CorpseViewers,
            EntityRegistry,
            Bindings,
            Readiness,
            StaticCollisionWorld,
            CollisionWorld,
            Logger,
            providedNetworkMetrics: NetworkMetrics,
            registrationLease: options.RegistrationLease,
            providedPerformanceMetrics: PerformanceMetrics,
            providedWorldActorStore: WorldActors,
            providedWorldActorActivityScheduler: WorldActorScheduler,
            providedWorldInteractionLeases: WorldInteractionLeases,
            providedWorldInteractionAuthority: WorldInteractionAuthority,
            providedWorldActorMetrics: WorldActorMetrics,
            workerIdentity: options.EnableWorldActors ? Identity : null,
            providedLiveMobCorpseStore: LiveMobCorpseStore,
            providedLiveMobCorpseInteractionService: LiveMobCorpseInteraction,
            providedCorpseRuntimeMetrics: CorpseRuntimeMetrics,
            itemCatalog: options.ItemCatalog,
            combatRules: options.CombatRules,
            shardRules: ShardRules,
            worldActorRuntime: WorldActorDocument,
            gameServiceClient: GameServiceClient,
            mobCorpses: mobCorpses,
            combatMetrics: CombatMetrics,
            lookStore: Looks,
            appearanceCatalog: AppearanceCatalog,
            developmentAdmin: options.EnableAdminTools ? new DevelopmentAdminOptions(true) : null);
    }

    // The checked-in appearance content, as the worker loads it.
    public static CharacterAppearanceCatalog AppearanceCatalog => LoadedAppearanceCatalog.Value;

    private static readonly Lazy<CharacterAppearanceCatalog> LoadedAppearanceCatalog = new(
        () => CharacterAppearanceFileLoader.Load(RepositoryPaths.CharacterAppearancePath));

    public CharacterLookStore Looks { get; } = new();

    public WorldActorRuntimeDocument? WorldActorDocument { get; }

    public SimulationShardRules ShardRules { get; } = new();

    public CombatMetrics CombatMetrics { get; }

    public RealtimeServerHarnessOptions Options { get; }

    public int Port { get; }

    public SimulationWorkerConfig Config { get; }

    public ScriptedGameServiceHandler GameService { get; }

    public GameServiceClient GameServiceClient { get; }

    public SimulationWorkerIdentity Identity { get; }

    public ChunkedStaticCollisionWorld StaticCollisionWorld { get; }

    public ICollisionWorld CollisionWorld { get; }

    public ActiveSimulationSessionStore SessionStore { get; } = new();

    public CharacterItemStateStore CharacterItemStateStore { get; } = new();

    public DurableCorpseStore CorpseStore { get; } = new(TimeProvider.System);

    public CorpseViewerRegistry CorpseViewers { get; } = new();

    public SimulationEntityRegistry EntityRegistry { get; } = new();

    public ConnectionEntityBindingRegistry Bindings { get; } = new();

    public RealtimeTransportReadiness Readiness { get; } = new();

    public RealtimeNetworkMetrics? NetworkMetrics { get; }

    public RealtimePerformanceMetrics? PerformanceMetrics { get; }

    public WorldActorStore? WorldActors { get; }

    public WorldActorActivityScheduler? WorldActorScheduler { get; }

    public WorldInteractionLeaseRegistry? WorldInteractionLeases { get; }

    public WorldInteractionAuthorityService? WorldInteractionAuthority { get; }

    public WorldActorMetrics? WorldActorMetrics { get; }

    public LiveMobCorpseStore? LiveMobCorpseStore { get; }

    public LiveMobCorpseInteractionService? LiveMobCorpseInteraction { get; }

    public CorpseRuntimeMetrics? CorpseRuntimeMetrics { get; }

    public CapturingLogger<RealtimeSimulationService> Logger { get; } = new();

    public RealtimeSimulationService Service { get; }

    public static RealtimeServerHarness Create(RealtimeServerHarnessOptions? options = null)
    {
        return new RealtimeServerHarness(options ?? new RealtimeServerHarnessOptions());
    }

    public static CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(PollTimeout);
        return timeout;
    }

    public RealtimeTestCharacter AddCharacter(string name, bool isSyntheticBot = false)
    {
        var character = new RealtimeTestCharacter($"ticket-{name}", name)
        {
            IsSyntheticBot = isSyntheticBot
        };
        GameService.AddCharacter(character);
        return character;
    }

    public async Task StartAsync()
    {
        using var timeout = CreateTimeout();
        await Service.StartAsync(timeout.Token);
        started = true;
        await Readiness.WaitUntilListeningAsync(timeout.Token);
    }

    public async Task StopAsync()
    {
        if (!started || stopped)
        {
            return;
        }

        stopped = true;
        foreach (var client in clients)
        {
            client.Stop();
        }

        await Service.StopAsync(CancellationToken.None);
    }

    public RealtimeRecordingClient Connect(string name, string? joinTicket = null)
    {
        var client = new RealtimeRecordingClient(name, joinTicket);
        clients.Add(client);
        client.Connect(Port);
        return client;
    }

    public async Task<RealtimeRecordingClient> JoinAsync(
        RealtimeTestCharacter character,
        string? clientName = null)
    {
        var client = Connect(clientName ?? character.Name, character.Ticket);
        await PollUntilAsync(
            () => client.JoinAccepted is not null,
            $"{client.Name} to join");
        return client;
    }

    public async Task PollUntilAsync(Func<bool> condition, string description)
    {
        using var timeout = CreateTimeout();
        while (true)
        {
            PollClients();
            if (condition())
            {
                return;
            }

            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {description}. {Describe()}");
            }
        }
    }

    // Keeps the clients polling for a fixed time, for assertions that nothing else arrives.
    public async Task PollForAsync(TimeSpan duration)
    {
        using var timeout = CreateTimeout();
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            PollClients();
            await Task.Delay(10, timeout.Token);
        }

        PollClients();
    }

    public void PollClients()
    {
        foreach (var client in clients)
        {
            client.PollEvents();
        }
    }

    public string Describe()
    {
        return string.Join(" | ", clients.Select(client => client.Describe()));
    }

    public void SeedDurableCorpse(
        Guid corpseId,
        double positionX,
        double positionY,
        double positionZ,
        long revision = 1)
    {
        var now = DateTime.UtcNow;
        CorpseStore.Apply(new DurableCorpseResponse(
            corpseId,
            null,
            "Fallen Hero",
            ShardId,
            positionX,
            positionY,
            positionZ,
            0d,
            0d,
            0d,
            1d,
            "player_corpse",
            revision,
            now.AddSeconds(-1),
            now.AddMinutes(5),
            false,
            [
                new CorpseSectionResponse("general_inventory", Guid.NewGuid(), 0, 1),
                new CorpseSectionResponse("equipment", Guid.NewGuid(), 0, 0),
                new CorpseSectionResponse("bag", Guid.NewGuid(), 0, 0)
            ]));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        foreach (var client in clients)
        {
            client.Dispose();
        }

        Service.Dispose();
        foreach (var disposable in disposables)
        {
            disposable.Dispose();
        }

        httpClient.Dispose();
    }

    public static SimulationWorkerConfig CreateConfig(
        int port,
        RealtimeServerHarnessOptions options)
    {
        return new SimulationWorkerConfig(
            WorkerId,
            "local-fleet",
            "local-node-1",
            ShardId,
            WorldId,
            "CollisionData",
            port,
            "127.0.0.1",
            port,
            16,
            options.JoinHandshakeTimeout,
            TimeSpan.FromMilliseconds(5),
            15,
            new MovementSimulationSettings(
                30,
                5f,
                720f,
                -24f,
                -2f,
                0f,
                -14f,
                14f,
                -14f,
                14f),
            new MovementSpawnConfig(0f, 0f, -1f, 0f),
            new Uri("http://game-service.test"),
            TimeSpan.FromSeconds(2),
            "test-simulation-worker-secret-at-least-32-characters",
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10),
            "127.0.0.1:6379",
            TimeSpan.FromSeconds(1))
        {
            UdpQuotas = options.UdpQuotas ?? UdpQuotaConfig.Default,
            InterestManagement = options.InterestManagement
                ?? InterestManagementConfig.Default
        };
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        disposables.Add(disposable);
        return disposable;
    }

    private static int FindAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}

// Answers the GameService routes the realtime service uses. Each route can be replaced
// per test, and the default answers succeed for registered characters.
internal sealed class ScriptedGameServiceHandler : HttpMessageHandler
{
    private readonly object sync = new();
    private readonly Dictionary<string, RealtimeTestCharacter> charactersByTicket =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, RealtimeTestCharacter> charactersBySession = [];
    private int consumeCount;
    private int releaseCount;
    private int itemOperationCount;
    private int corpseOpenCount;
    private int corpseMutationCount;

    public ScriptedGameServiceHandler()
    {
        OnConsume = (request, _) => Task.FromResult(DefaultConsume(request));
        OnRelease = (sessionId, _, _) => Task.FromResult(DefaultRelease(sessionId));
        OnItemOperation = (_, request, _) => Task.FromResult(
            CommittedItemOperation(request, 1, 0, 200));
        OnCorpseOpen = (_, _, _, _) => Task.FromResult(
            Problem(HttpStatusCode.NotFound, "corpse_not_found", "The corpse is not active."));
        OnCorpseMutation = (_, _, _, _) => Task.FromResult(
            Problem(HttpStatusCode.NotFound, "corpse_not_found", "The corpse is not active."));
        OnMobLootGrant = (_, request, _) => Task.FromResult(
            CommittedMobLootGrant(request, 1, 1, 200));
        OnAmmunitionConsumption = (_, request, _) => Task.FromResult(DefaultAmmunitionConsumption(request));
        OnPlayerDeath = (_, request, _) => Task.FromResult(DefaultPlayerDeath(request));
        OnAppearanceChange = (sessionId, request, _) => Task.FromResult(DefaultAppearanceChange(sessionId, request));
    }

    public Func<Guid, SimulationAppearanceChangeRequest, CancellationToken, Task<HttpResponseMessage>>
        OnAppearanceChange
    { get; set; }

    public List<SimulationAppearanceChangeRequest> AppearanceChanges { get; } = [];

    private readonly Dictionary<Guid, SimulationAppearance> appearanceBySession = [];

    // Stores the change on the session's look as GameService would.
    public HttpResponseMessage DefaultAppearanceChange(Guid simulationSessionId, SimulationAppearanceChangeRequest request)
    {
        SimulationAppearance changed;
        lock (sync)
        {
            if (!charactersBySession.TryGetValue(simulationSessionId, out var character))
            {
                return Problem(HttpStatusCode.Unauthorized, "invalid_simulation_session", "The session is not active.");
            }

            var current = appearanceBySession.GetValueOrDefault(simulationSessionId, character.Appearance);
            changed = request.Change == "headwear"
                ? current with { HeadwearLowered = request.HeadwearLowered!.Value }
                : current with { BodyModel = request.BodyModel!, Choices = request.Choices!.ToArray() };
            appearanceBySession[simulationSessionId] = changed;
        }

        return Json(changed);
    }

    public Func<Guid, SimulationAmmunitionConsumptionRequest, CancellationToken, Task<HttpResponseMessage>>
        OnAmmunitionConsumption
    { get; set; }

    public Func<Guid, SimulationPlayerDeathRequest, CancellationToken, Task<HttpResponseMessage>>
        OnPlayerDeath
    { get; set; }

    private readonly Dictionary<Guid, SimulationEquippedItem[]> equipmentByCharacter = [];

    public List<SimulationAmmunitionConsumptionRequest> AmmunitionConsumptions { get; } = [];

    public List<SimulationPlayerDeathRequest> PlayerDeaths { get; } = [];

    // Commits a batch against the character's equipment as the join ticket named it.
    public HttpResponseMessage DefaultAmmunitionConsumption(SimulationAmmunitionConsumptionRequest request)
    {
        SimulationEquippedItem[] equipment;
        lock (sync)
        {
            if (!equipmentByCharacter.TryGetValue(request.CharacterId, out var current))
            {
                current = charactersBySession.Values
                    .FirstOrDefault(value => value.CharacterId == request.CharacterId)?.Equipment.ToArray() ?? [];
            }

            equipment = current
                .Select(item => item.ItemInstanceId == request.ItemInstanceId
                    ? item with { Quantity = item.Quantity - request.Quantity, Revision = request.ExpectedItemRevision + 1 }
                    : item)
                .Where(item => item.Quantity > 0)
                .ToArray();
            equipmentByCharacter[request.CharacterId] = equipment;
        }

        var revision = request.ExpectedCharacterRevision + 1;
        var remaining = equipment.FirstOrDefault(item => item.ItemInstanceId == request.ItemInstanceId);
        return Json(new SimulationItemTransactionResponse(
            request.OperationId,
            "consume_equipped_ammunition",
            true,
            null,
            [new SimulationItemCharacterRevision(request.CharacterId, revision, 0, 200)],
            [],
            remaining is null ? [] : [new SimulationItemInstanceRevision(remaining.ItemInstanceId, remaining.Revision)],
            [],
            CharacterStateTestData.Create(request.CharacterId, revision, equipment: equipment)));
    }

    // Partitions every item to a corpse at the place of death.
    public static HttpResponseMessage DefaultPlayerDeath(SimulationPlayerDeathRequest request)
    {
        var now = DateTime.UtcNow;
        var revision = request.ExpectedCharacterRevision + 1;
        return Json(new PlayerDeathPartitionResponse(
            request.OperationId,
            request.DeathEventId,
            new DurableCorpseResponse(
                Guid.NewGuid(),
                request.CharacterId,
                "Fallen Hero",
                request.ShardId,
                request.PositionX,
                request.PositionY,
                request.PositionZ,
                request.RotationX,
                request.RotationY,
                request.RotationZ,
                request.RotationW,
                request.PresentationKey,
                1,
                now,
                now.AddMinutes(5),
                true,
                [
                    new CorpseSectionResponse("general_inventory", Guid.NewGuid(), 0, 0),
                    new CorpseSectionResponse("equipment", Guid.NewGuid(), 0, 0),
                    new CorpseSectionResponse("bag", Guid.NewGuid(), 0, 0)
                ]),
            new SimulationItemCharacterRevision(request.CharacterId, revision, 0, 200),
            [],
            CharacterStateTestData.Create(request.CharacterId, revision)));
    }

    public Func<ConsumeSimulationJoinTicketRequest, CancellationToken, Task<HttpResponseMessage>>
        OnConsume
    { get; set; }

    public Func<Guid, SimulationSessionCredentialRequest, CancellationToken, Task<HttpResponseMessage>>
        OnRelease
    { get; set; }

    public Func<Guid, SimulationItemOperationRequest, CancellationToken, Task<HttpResponseMessage>>
        OnItemOperation
    { get; set; }

    public Func<Guid, Guid, SimulationCorpseOpenRequest, CancellationToken, Task<HttpResponseMessage>>
        OnCorpseOpen
    { get; set; }

    public Func<Guid, Guid, SimulationCorpseMutationRequest, CancellationToken, Task<HttpResponseMessage>>
        OnCorpseMutation
    { get; set; }

    public Func<Guid, SimulationMobLootGrantRequest, CancellationToken, Task<HttpResponseMessage>>
        OnMobLootGrant
    { get; set; }

    public int ConsumeCount => Volatile.Read(ref consumeCount);

    public int ReleaseCount => Volatile.Read(ref releaseCount);

    public int ItemOperationCount => Volatile.Read(ref itemOperationCount);

    public int CorpseOpenCount => Volatile.Read(ref corpseOpenCount);

    public int CorpseMutationCount => Volatile.Read(ref corpseMutationCount);

    public void AddCharacter(RealtimeTestCharacter character)
    {
        lock (sync)
        {
            charactersByTicket[character.Ticket] = character;
            charactersBySession[character.SimulationSessionId] = character;
        }
    }

    public HttpResponseMessage DefaultConsume(ConsumeSimulationJoinTicketRequest request)
    {
        RealtimeTestCharacter? character;
        lock (sync)
        {
            charactersByTicket.TryGetValue(request.Ticket, out character);
        }

        if (character is null)
        {
            return Problem(
                HttpStatusCode.NotFound,
                "join_ticket_not_found",
                "The join ticket is not active.");
        }

        return Json(new ConsumedSimulationJoinTicketResponse(
            character.AccountId,
            character.CharacterId,
            character.Name,
            RealtimeServerHarness.ShardId,
            RealtimeServerHarness.WorldId,
            RealtimeServerHarness.WorkerId,
            RealtimeServerHarness.RuntimeId,
            character.SimulationSessionId,
            character.SimulationSessionToken,
            DateTime.UtcNow.AddMilliseconds(character.SessionLeaseDurationMilliseconds),
            character.ItemStateRevision,
            character.CarriedWeight,
            character.CarryCapacity,
            false)
        {
            SessionLeaseDurationMilliseconds = character.SessionLeaseDurationMilliseconds,
            IsSyntheticBot = character.IsSyntheticBot,
            Equipment = character.Equipment,
            Appearance = character.IsSyntheticBot ? null : character.Appearance
        });
    }

    public HttpResponseMessage DefaultRelease(Guid simulationSessionId)
    {
        RealtimeTestCharacter? character;
        lock (sync)
        {
            charactersBySession.TryGetValue(simulationSessionId, out character);
        }

        return Json(new SimulationSessionLeaseResponse(
            simulationSessionId,
            character?.CharacterId ?? Guid.NewGuid(),
            RealtimeServerHarness.ShardId,
            RealtimeServerHarness.WorkerId,
            RealtimeServerHarness.RuntimeId,
            DateTime.UtcNow,
            character?.ItemStateRevision ?? 0,
            character?.CarriedWeight ?? 0,
            character?.CarryCapacity ?? 200,
            true)
        {
            LeaseDurationMilliseconds = 30_000
        });
    }

    public static HttpResponseMessage CommittedItemOperation(
        SimulationItemOperationRequest request,
        long revision,
        long carriedWeight,
        long carryCapacity)
    {
        return Json(new SimulationItemTransactionResponse(
            request.OperationId,
            request.OperationKind,
            true,
            null,
            [new SimulationItemCharacterRevision(
                request.CharacterId,
                revision,
                carriedWeight,
                carryCapacity)],
            [],
            [],
            [],
            CharacterStateTestData.Create(
                request.CharacterId,
                revision,
                carriedWeight,
                carryCapacity)));
    }

    // Grants the whole loot entry into the requested empty destination slot.
    public static HttpResponseMessage CommittedMobLootGrant(
        SimulationMobLootGrantRequest request,
        long revision,
        long carriedWeight,
        long carryCapacity)
    {
        return Json(new SimulationItemTransactionResponse(
            request.GrantId,
            "grant_mob_loot",
            true,
            null,
            [new SimulationItemCharacterRevision(
                request.CharacterId,
                revision,
                carriedWeight,
                carryCapacity)],
            [new SimulationItemContainerRevision(
                request.DestinationContainerId,
                request.ExpectedDestinationContainerRevision + 1)],
            [new SimulationItemInstanceRevision(Guid.NewGuid(), 0)],
            [],
            CharacterStateTestData.Create(
                request.CharacterId,
                revision,
                carriedWeight,
                carryCapacity)));
    }

    public static HttpResponseMessage Json<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };
    }

    public static HttpResponseMessage Problem(
        HttpStatusCode statusCode,
        string code,
        string detail)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = JsonContent.Create(new { code, detail })
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var segments = request.RequestUri!.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.RequestUri.AbsolutePath == "/api/simulation-join-tickets/consume")
        {
            Interlocked.Increment(ref consumeCount);
            var consume = await request.Content!
                .ReadFromJsonAsync<ConsumeSimulationJoinTicketRequest>(cancellationToken);
            return await OnConsume(consume!, cancellationToken);
        }

        if (segments.Length >= 4
            && segments[0] == "api"
            && segments[1] == "simulation-sessions"
            && Guid.TryParse(segments[2], out var simulationSessionId))
        {
            if (segments.Length == 4 && segments[3] == "release")
            {
                Interlocked.Increment(ref releaseCount);
                var release = await request.Content!
                    .ReadFromJsonAsync<SimulationSessionCredentialRequest>(cancellationToken);
                return await OnRelease(simulationSessionId, release!, cancellationToken);
            }

            if (segments.Length == 4 && segments[3] == "ammunition-consumptions")
            {
                var consumption = await request.Content!
                    .ReadFromJsonAsync<SimulationAmmunitionConsumptionRequest>(cancellationToken);
                lock (sync)
                {
                    AmmunitionConsumptions.Add(consumption!);
                }

                return await OnAmmunitionConsumption(simulationSessionId, consumption!, cancellationToken);
            }

            if (segments.Length == 4 && segments[3] == "player-deaths")
            {
                var death = await request.Content!
                    .ReadFromJsonAsync<SimulationPlayerDeathRequest>(cancellationToken);
                lock (sync)
                {
                    PlayerDeaths.Add(death!);
                }

                return await OnPlayerDeath(simulationSessionId, death!, cancellationToken);
            }

            if (segments.Length == 4 && segments[3] == "mob-loot-grants")
            {
                var grant = await request.Content!
                    .ReadFromJsonAsync<SimulationMobLootGrantRequest>(cancellationToken);
                return await OnMobLootGrant(simulationSessionId, grant!, cancellationToken);
            }

            if (segments.Length == 4 && segments[3] == "appearance")
            {
                var change = await request.Content!
                    .ReadFromJsonAsync<SimulationAppearanceChangeRequest>(cancellationToken);
                lock (sync)
                {
                    AppearanceChanges.Add(change!);
                }

                return await OnAppearanceChange(simulationSessionId, change!, cancellationToken);
            }

            if (segments.Length == 4 && segments[3] == "item-operations")
            {
                Interlocked.Increment(ref itemOperationCount);
                var operation = await request.Content!
                    .ReadFromJsonAsync<SimulationItemOperationRequest>(cancellationToken);
                return await OnItemOperation(simulationSessionId, operation!, cancellationToken);
            }

            if (segments.Length == 6
                && segments[3] == "corpses"
                && Guid.TryParse(segments[4], out var corpseId))
            {
                if (segments[5] == "open")
                {
                    Interlocked.Increment(ref corpseOpenCount);
                    var open = await request.Content!
                        .ReadFromJsonAsync<SimulationCorpseOpenRequest>(cancellationToken);
                    return await OnCorpseOpen(
                        simulationSessionId,
                        corpseId,
                        open!,
                        cancellationToken);
                }

                if (segments[5] == "item-operations")
                {
                    Interlocked.Increment(ref corpseMutationCount);
                    var mutation = await request.Content!
                        .ReadFromJsonAsync<SimulationCorpseMutationRequest>(cancellationToken);
                    return await OnCorpseMutation(
                        simulationSessionId,
                        corpseId,
                        mutation!,
                        cancellationToken);
                }
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

// Lets a test hold a GameService call open and observe that it started.
internal sealed class TestGate
{
    private readonly TaskCompletionSource entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int entryCount;

    public bool HasEntered => entered.Task.IsCompleted;

    public int EntryCount => Volatile.Read(ref entryCount);

    public async Task WaitAsync()
    {
        Interlocked.Increment(ref entryCount);
        entered.TrySetResult();
        await released.Task;
    }

    public void Release()
    {
        released.TrySetResult();
    }
}

// LoggedAtUtc lets timing assertions anchor on when the server acted, which client-side
// polling delays cannot move.
internal sealed record CapturedLogEntry(
    LogLevel Level,
    string? Template,
    string Message,
    Exception? Exception,
    DateTime LoggedAtUtc);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object sync = new();
    private readonly List<CapturedLogEntry> entries = [];

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (sync)
            {
                return entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
            ? values.FirstOrDefault(value => value.Key == "{OriginalFormat}").Value as string
            : null;
        lock (sync)
        {
            entries.Add(new CapturedLogEntry(
                logLevel,
                template,
                formatter(state, exception),
                exception,
                DateTime.UtcNow));
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private long utcNowTicks = utcNow.UtcTicks;

    public void Advance(TimeSpan duration)
    {
        Interlocked.Add(ref utcNowTicks, duration.Ticks);
    }

    public override DateTimeOffset GetUtcNow()
    {
        return new DateTimeOffset(Interlocked.Read(ref utcNowTicks), TimeSpan.Zero);
    }
}

internal static class RealtimeCorpseTestData
{
    public static CorpseViewSnapshotResponse Snapshot(
        Guid corpseId,
        long revision,
        double positionX = 1d,
        double positionY = 0d,
        double positionZ = -1d,
        Guid? itemInstanceId = null)
    {
        var now = DateTime.UtcNow;
        return new CorpseViewSnapshotResponse(
            corpseId,
            null,
            "Fallen Hero",
            RealtimeServerHarness.ShardId,
            positionX,
            positionY,
            positionZ,
            "player_corpse",
            revision,
            now.AddSeconds(-1),
            now.AddMinutes(5),
            [
                new CorpseViewSectionResponse(
                    "general_inventory",
                    GeneralContainerId(corpseId),
                    "corpse_general_inventory",
                    revision,
                    1,
                    [
                        new CorpseViewSlotResponse(
                            0,
                            "general",
                            [],
                            itemInstanceId is null
                                ? null
                                : new CorpseViewItemResponse(
                                    itemInstanceId.Value,
                                    "fixture.ore",
                                    1,
                                    revision,
                                    null,
                                    null))
                    ]),
                new CorpseViewSectionResponse(
                    "equipment",
                    EquipmentContainerId(corpseId),
                    "corpse_equipment",
                    0,
                    1,
                    [new CorpseViewSlotResponse(0, "equipment", [], null, "chest")]),
                new CorpseViewSectionResponse(
                    "bag",
                    BagContainerId(corpseId),
                    "corpse_bag",
                    0,
                    1,
                    [new CorpseViewSlotResponse(0, "bag", [], null)])
            ],
            Array.Empty<CorpsePresentationSnapshotResponse>());
    }

    private static Guid GeneralContainerId(Guid corpseId) => Derive(corpseId, 1);

    private static Guid EquipmentContainerId(Guid corpseId) => Derive(corpseId, 2);

    private static Guid BagContainerId(Guid corpseId) => Derive(corpseId, 3);

    private static Guid Derive(Guid corpseId, byte marker)
    {
        var bytes = corpseId.ToByteArray();
        bytes[0] ^= marker;
        return new Guid(bytes);
    }
}
