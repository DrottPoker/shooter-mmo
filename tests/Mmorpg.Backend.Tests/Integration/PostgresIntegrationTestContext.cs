using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Auth;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Redis;
using Mmorpg.GameService.Simulation;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Worlds;
using Npgsql;

namespace Mmorpg.Backend.Tests.Integration;

internal sealed class PostgresIntegrationTestContext : IAsyncDisposable
{
    private readonly ServiceProvider serviceProvider;
    private readonly AsyncServiceScope scope;

    private PostgresIntegrationTestContext(
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        GameServiceConfiguration gameServiceConfiguration)
    {
        DataSource = dataSource;
        Configuration = configuration;
        GameServiceConfiguration = gameServiceConfiguration;

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddGameServiceForTests(dataSource, gameServiceConfiguration);
        serviceProvider = services.BuildServiceProvider(validateScopes: true);
        scope = serviceProvider.CreateAsyncScope();
        Services = scope.ServiceProvider;

        CatalogSource = Services.GetRequiredService<ItemCatalogSource>();
        ItemCatalogSeeder = Services.GetRequiredService<ItemCatalogSeeder>();
        CharacterItemStateBootstrapper = Services.GetRequiredService<CharacterItemStateBootstrapper>();
        ItemCatalogQueryService = Services.GetRequiredService<ItemCatalogQueryService>();
        ItemQueryService = Services.GetRequiredService<ItemQueryService>();
        ItemTransactionService = Services.GetRequiredService<ItemTransactionService>();
        AccountItemMutationService = Services.GetRequiredService<AccountItemMutationService>();
        SimulationItemMutationService = Services.GetRequiredService<SimulationItemMutationService>();
        CorpseService = Services.GetRequiredService<CorpseService>();
        ItemPolicyService = Services.GetRequiredService<ItemPolicyService>();
        QuestItemService = Services.GetRequiredService<QuestItemService>();
        DatabaseInitializer = Services.GetRequiredService<DatabaseInitializer>();
        SessionService = Services.GetRequiredService<SessionService>();
        AccountService = Services.GetRequiredService<AccountService>();
        CharacterService = Services.GetRequiredService<CharacterService>();
        ShardService = Services.GetRequiredService<ShardService>();
        SimulationSessionService = Services.GetRequiredService<SimulationSessionService>();
        SimulationWorkerRegistryService = Services.GetRequiredService<SimulationWorkerRegistryService>();
        SimulationTopologySeeder = Services.GetRequiredService<SimulationTopologySeeder>();
    }

    public NpgsqlDataSource DataSource { get; }

    // The production service graph, built by GameServiceTestComposition.
    public IServiceProvider Services { get; }

    public DatabaseInitializer DatabaseInitializer { get; }

    public ItemCatalogSource CatalogSource { get; }

    public ItemCatalogSeeder ItemCatalogSeeder { get; }

    public CharacterItemStateBootstrapper CharacterItemStateBootstrapper { get; }

    public ItemCatalogQueryService ItemCatalogQueryService { get; }

    public ItemQueryService ItemQueryService { get; }

    public ItemTransactionService ItemTransactionService { get; }

    public AccountItemMutationService AccountItemMutationService { get; }

    public SimulationItemMutationService SimulationItemMutationService { get; }

    public CorpseService CorpseService { get; }

    public ItemPolicyService ItemPolicyService { get; }

    public QuestItemService QuestItemService { get; }

    public IConfiguration Configuration { get; }

    public GameServiceConfiguration GameServiceConfiguration { get; }

    public SessionService SessionService { get; }

    public AccountService AccountService { get; }

    public CharacterService CharacterService { get; }

    public ShardService ShardService { get; }

    public SimulationSessionService SimulationSessionService { get; }

    public SimulationWorkerRegistryService SimulationWorkerRegistryService { get; }

    public SimulationTopologySeeder SimulationTopologySeeder { get; }

    public static async Task<PostgresIntegrationTestContext> CreateAsync(
        bool initializeDatabase = true,
        bool heartbeatSeedShard = true)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgresIntegrationFactAttribute.ConnectionStringVariable)!;

        EnsureDedicatedTestDatabase(connectionString);

        var dataSource = NpgsqlDataSource.Create(connectionString);

        try
        {
            await ResetPublicSchemaAsync(dataSource);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:SessionLifetimeHours"] = "24",
                    ["Game:MaxCharactersPerAccount"] = "5",
                    ["Simulation:JoinTicketLifetimeSeconds"] = "30",
                    ["Simulation:SessionLeaseLifetimeSeconds"] = "60",
                    ["Simulation:WorkerHeartbeatTimeoutSeconds"] = "30",
                    ["Items:CatalogPath"] = Path.Combine(
                        AppContext.BaseDirectory,
                        "WorldData",
                        "Items",
                        "core.item-catalog.json")
                })
                .Build();

            var topology = new SimulationTopologyConfig(
                [new FleetBootstrapConfig("local-fleet", "Local Development", "LOCAL")],
                [new SimulationNodeBootstrapConfig("local-node-1", "local-fleet", "Local Node 1")],
                [new ShardBootstrapConfig(
                    "local-shard-1",
                    "local-fleet",
                    "Local Shard 1",
                    "mvp-open-risk")]);
            var gameServiceConfig = new GameServiceConfig(
                connectionString,
                "localhost:6379",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                topology);
            var gameServiceConfiguration = new GameServiceConfiguration(
                gameServiceConfig,
                LoadWorldManifestCatalog(),
                new DevelopmentSimulationBotOptions(
                    false,
                    string.Empty,
                    TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(30),
                    "Active Bot"),
                new StackStressFixtureOptions(false, string.Empty),
                NpcItemLifecycleOptions.Default,
                ItemOperationsOptions.CreateDefaults(),
                DisabledRedisAcceleration(),
                ItemCatalogSource.FromConfiguration(configuration));
            var context = new PostgresIntegrationTestContext(
                dataSource,
                configuration,
                gameServiceConfiguration);
            if (initializeDatabase)
            {
                await context.InitializeDatabaseAsync();
                if (heartbeatSeedShard)
                {
                    var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
                        "local-simulation-worker-1",
                        CreateHeartbeatRequest(),
                        CancellationToken.None);
                    Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
                }
            }

            return context;
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeDatabaseAsync()
    {
        await DatabaseInitializer.InitializeAsync(CancellationToken.None);
        await SimulationTopologySeeder.SeedAsync(CancellationToken.None);
    }

    public static SimulationWorkerHeartbeatRequest CreateHeartbeatRequest(
        string runtimeId = "integration-worker-runtime",
        string host = "127.0.0.1",
        int udpPort = 27015,
        string workerShardId = "local-shard-1")
    {
        return new SimulationWorkerHeartbeatRequest(
            "local-fleet",
            "local-node-1",
            workerShardId,
            "development-world-1",
            runtimeId,
            DateTime.UtcNow.AddMinutes(-1),
            host,
            udpPort,
            100,
            0,
            RealtimeProtocol.Version,
            GameSimulationCompatibility.Revision,
            "integration-collision-revision");
    }

    // Tests never reach Redis, so session lookups and rate limits use PostgreSQL only.
    private static RedisAccelerationOptions DisabledRedisAcceleration()
    {
        return new RedisAccelerationOptions(
            false,
            "shooter-mmo",
            false,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
            false,
            TimeSpan.FromSeconds(30));
    }

    private static WorldManifestCatalog LoadWorldManifestCatalog()
    {
        return WorldManifestCatalog.FromManifests(
            WorldManifestFileStore.LoadAll(RepositoryPaths.Worlds));
    }

    public async Task<IntegrationPlayer> RegisterPlayerAsync(
        string email = "integration@example.com",
        string username = "integration_player",
        string characterName = "Integration Hero")
    {
        var registration = await AccountService.RegisterAsync(
            new RegisterAccountRequest(email, username, "TestPass123!"),
            CancellationToken.None);

        Assert.True(registration.Succeeded, registration.Error?.Message);

        var authenticatedAccount = await SessionService.AuthenticateTokenAsync(
            registration.Value!.SessionToken,
            CancellationToken.None);

        Assert.NotNull(authenticatedAccount);

        var character = await CharacterService.CreateAsync(
            registration.Value.AccountId,
            new CreateCharacterRequest(characterName),
            CancellationToken.None);

        Assert.True(character.Succeeded, character.Error?.Message);

        return new IntegrationPlayer(
            registration.Value,
            authenticatedAccount!,
            character.Value!);
    }

    public async Task AddShardAsync(string shardId)
    {
        await using var command = DataSource.CreateCommand(
            """
            insert into shards (id, display_name, world_id, fleet_id, rule_set)
            values (@ShardId, @DisplayName, 'development-world-1', 'local-fleet', 'mvp-open-risk');
            """);

        command.Parameters.AddWithValue("ShardId", shardId);
        command.Parameters.AddWithValue("DisplayName", shardId);
        await command.ExecuteNonQueryAsync();

        var workerId = $"worker-{shardId}";
        var heartbeat = await SimulationWorkerRegistryService.HeartbeatAsync(
            workerId,
            CreateHeartbeatRequest(
                runtimeId: $"runtime-{shardId}",
                udpPort: 27016,
                workerShardId: shardId),
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
    }

    public async Task<int> ExecuteScalarIntAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task<string?> ExecuteScalarStringAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await serviceProvider.DisposeAsync();
        await DataSource.DisposeAsync();
    }

    private static void EnsureDedicatedTestDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The PostgreSQL integration connection must target a database whose name contains 'test'.");
        }
    }

    private static async Task ResetPublicSchemaAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "drop schema if exists public cascade; create schema public;");

        await command.ExecuteNonQueryAsync();
    }
}

internal sealed record IntegrationPlayer(
    AuthResponse Registration,
    AuthenticatedAccount Account,
    CharacterResponse Character);
