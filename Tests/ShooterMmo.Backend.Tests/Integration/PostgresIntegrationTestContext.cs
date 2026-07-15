using AuthService.Auth;
using AuthService.Characters;
using AuthService.Config;
using AuthService.Database;
using AuthService.Items;
using AuthService.Simulation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Integration;

internal sealed class PostgresIntegrationTestContext : IAsyncDisposable
{
    private PostgresIntegrationTestContext(
        NpgsqlDataSource dataSource,
        IConfiguration configuration,
        AuthServiceConfig authServiceConfig)
    {
        DataSource = dataSource;
        CatalogSource = ItemCatalogSource.FromConfiguration(configuration);
        ItemCatalogSeeder = new ItemCatalogSeeder(
            dataSource,
            CatalogSource,
            NullLogger<ItemCatalogSeeder>.Instance);
        CharacterItemStateBootstrapper = new CharacterItemStateBootstrapper(dataSource);
        ItemCatalogQueryService = new ItemCatalogQueryService(dataSource);
        ItemQueryService = new ItemQueryService(dataSource);
        ItemTransactionService = new ItemTransactionService(dataSource);
        DatabaseInitializer = new DatabaseInitializer(
            dataSource,
            ItemCatalogSeeder,
            CharacterItemStateBootstrapper,
            NullLogger<DatabaseInitializer>.Instance);
        SessionService = new SessionService(dataSource, configuration);
        AccountService = new AccountService(
            dataSource,
            SessionService,
            NullLogger<AccountService>.Instance);
        CharacterService = new CharacterService(
            dataSource,
            configuration,
            CharacterItemStateBootstrapper);
        ShardService = new ShardService(dataSource, configuration, authServiceConfig);
        SimulationSessionService = new SimulationSessionService(dataSource, configuration);
        SimulationWorkerRegistryService = new SimulationWorkerRegistryService(dataSource, authServiceConfig);
        SimulationTopologySeeder = new SimulationTopologySeeder(
            dataSource,
            authServiceConfig,
            NullLogger<SimulationTopologySeeder>.Instance);
    }

    public NpgsqlDataSource DataSource { get; }

    public DatabaseInitializer DatabaseInitializer { get; }

    public ItemCatalogSource CatalogSource { get; }

    public ItemCatalogSeeder ItemCatalogSeeder { get; }

    public CharacterItemStateBootstrapper CharacterItemStateBootstrapper { get; }

    public ItemCatalogQueryService ItemCatalogQueryService { get; }

    public ItemQueryService ItemQueryService { get; }

    public ItemTransactionService ItemTransactionService { get; }

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
                [new WorldDefinitionBootstrapConfig("local-world-1", "Local Test World")],
                [new FleetBootstrapConfig("local-fleet", "Local Development", "LOCAL")],
                [new SimulationNodeBootstrapConfig("local-node-1", "local-fleet", "Local Node 1")],
                [new ShardBootstrapConfig(
                    "local-shard-1",
                    "local-world-1",
                    "local-fleet",
                    "Local Shard 1",
                    "mvp-open-risk")]);
            var authServiceConfig = new AuthServiceConfig(
                connectionString,
                "localhost:6379",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30),
                topology);
            var context = new PostgresIntegrationTestContext(dataSource, configuration, authServiceConfig);
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
            runtimeId,
            DateTime.UtcNow.AddMinutes(-1),
            host,
            udpPort,
            100,
            0,
            6,
            "movement-simulation-v2",
            "integration-collision-revision");
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
            values (@ShardId, @DisplayName, 'local-world-1', 'local-fleet', 'mvp-open-risk');
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

    public async Task ExecuteAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        return DataSource.DisposeAsync();
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
