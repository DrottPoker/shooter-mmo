using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameProtocol;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.GameSimulation;
using Mmorpg.Server.Common.Items;
using Mmorpg.Server.Common.Worlds;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.GameService.Testing;

internal sealed class PostgresIntegrationTestContext : IAsyncDisposable
{
    // Built once per test run, then copied for every context that initializes its database.
    private static readonly PostgresTestTemplate InitializedTemplate = new(
        "initialized",
        InitializeTemplateAsync);

    private readonly PostgresTestDatabaseLease? lease;
    private readonly ServiceProvider serviceProvider;
    private readonly AsyncServiceScope scope;

    private PostgresIntegrationTestContext(
        PostgresTestDatabaseLease? lease,
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        GameServiceConfiguration gameServiceConfiguration)
    {
        this.lease = lease;
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
        ItemPolicyService = new ItemPolicyService(ItemTransactionService);
        QuestItemService = new QuestItemService(ItemTransactionService);
        DatabaseInitializer = Services.GetRequiredService<GameServiceDatabaseInitializer>();
        SessionService = Services.GetRequiredService<SessionService>();
        AccountService = Services.GetRequiredService<AccountService>();
        CharacterService = Services.GetRequiredService<CharacterService>();
        ShardService = Services.GetRequiredService<ShardService>();
        SimulationSessionService = Services.GetRequiredService<SimulationSessionService>();
        SimulationWorkerRegistryService = Services.GetRequiredService<SimulationWorkerRegistryService>();
        SimulationTopologySeeder = Services.GetRequiredService<SimulationTopologySeeder>();
    }

    public NpgsqlDataSource DataSource { get; }

    // The full connection string of this test assembly's database, for tests that build
    // their own data source. DataSource.ConnectionString omits the password.
    public string ConnectionString => GameServiceConfiguration.Platform.PostgresConnectionString;

    // The production service graph, built by GameServiceTestComposition.
    public IServiceProvider Services { get; }

    public GameServiceDatabaseInitializer DatabaseInitializer { get; }

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

    // Every context owns a fresh database: an empty public schema, or with initializeDatabase
    // a copy of the initialized template, which equals a database that just ran
    // InitializeDatabaseAsync.
    public static async Task<PostgresIntegrationTestContext> CreateAsync(
        bool initializeDatabase = true,
        bool heartbeatSeedShard = true)
    {
        var lease = await PostgresTestDatabase.LeaseAsync(
            initializeDatabase ? InitializedTemplate : PostgresTestTemplate.Empty);
        PostgresIntegrationTestContext? context = null;
        try
        {
            context = Create(lease.ConnectionString, lease);
            if (initializeDatabase && heartbeatSeedShard)
            {
                var heartbeat = await context.SimulationWorkerRegistryService.HeartbeatAsync(
                    "local-simulation-worker-1",
                    CreateHeartbeatRequest(),
                    CancellationToken.None);
                Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
            }

            return context;
        }
        catch
        {
            if (context is null)
            {
                await lease.DisposeAsync();
            }
            else
            {
                await context.DisposeAsync();
            }

            throw;
        }
    }

    // A null lease leaves the database to the caller, as the template build does.
    private static PostgresIntegrationTestContext Create(
        string connectionString,
        PostgresTestDatabaseLease? lease)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:SessionLifetimeHours"] = "24",
                    ["Game:MaxCharactersPerAccount"] = "5",
                    ["Simulation:JoinTicketLifetimeSeconds"] = "30",
                    ["Simulation:SessionLeaseLifetimeSeconds"] = "60",
                    ["Simulation:WorkerHeartbeatTimeoutSeconds"] = "30",
                    ["Items:CatalogPath"] = RepositoryPaths.ItemCatalogFixtureRuntimePath
                })
                .Build();

            return new PostgresIntegrationTestContext(
                lease,
                dataSource,
                configuration,
                GameServiceTestComposition.CreateConfiguration(connectionString));
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    // Migrations and startup seeds, through the same service graph and configuration as
    // every test context. The worker heartbeat stays per test, because it records the
    // current time.
    private static async Task InitializeTemplateAsync(string connectionString)
    {
        await using var context = Create(connectionString, lease: null);
        await context.InitializeDatabaseAsync();
    }

    public async Task InitializeDatabaseAsync()
    {
        await DatabaseInitializer.InitializeAsync(CancellationToken.None);
        await SimulationTopologySeeder.SeedAsync(CancellationToken.None);
    }

    // The fixture catalog GameService mirrors in tests, which a worker heartbeat reports.
    public static string FixtureItemCatalogRevision => FixtureCatalogRevision.Value;

    private static readonly Lazy<string> FixtureCatalogRevision = new(() =>
        ItemCatalogFileLoader.Load(RepositoryPaths.ItemCatalogFixtureRuntimePath).Revision);

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
            "integration-collision-revision",
            FixtureItemCatalogRevision);
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
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }
}

internal sealed record IntegrationPlayer(
    AuthResponse Registration,
    AuthenticatedAccount Account,
    CharacterResponse Character);
