using AuthService.Auth;
using AuthService.Characters;
using AuthService.Database;
using AuthService.Worlds;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Integration;

internal sealed class PostgresIntegrationTestContext : IAsyncDisposable
{
    private PostgresIntegrationTestContext(NpgsqlDataSource dataSource, IConfiguration configuration)
    {
        DataSource = dataSource;
        SessionService = new SessionService(dataSource, configuration);
        AccountService = new AccountService(dataSource, SessionService);
        CharacterService = new CharacterService(dataSource, configuration);
        WorldService = new WorldService(dataSource, configuration);
        WorldSessionService = new WorldSessionService(dataSource, configuration);
    }

    public NpgsqlDataSource DataSource { get; }

    public SessionService SessionService { get; }

    public AccountService AccountService { get; }

    public CharacterService CharacterService { get; }

    public WorldService WorldService { get; }

    public WorldSessionService WorldSessionService { get; }

    public static async Task<PostgresIntegrationTestContext> CreateAsync(bool initializeDatabase = true)
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
                    ["WorldJoin:TicketLifetimeSeconds"] = "30",
                    ["WorldSession:LeaseLifetimeSeconds"] = "60"
                })
                .Build();

            var context = new PostgresIntegrationTestContext(dataSource, configuration);
            if (initializeDatabase)
            {
                await context.InitializeDatabaseAsync();
            }

            return context;
        }
        catch
        {
            await dataSource.DisposeAsync();
            throw;
        }
    }

    public Task InitializeDatabaseAsync()
    {
        var initializer = new DatabaseInitializer(
            DataSource,
            NullLogger<DatabaseInitializer>.Instance);

        return initializer.InitializeAsync(CancellationToken.None);
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

    public async Task AddWorldAsync(string worldId)
    {
        await using var command = DataSource.CreateCommand(
            """
            insert into worlds (id, display_name, host, udp_port, rule_set, is_online)
            values (@WorldId, @DisplayName, '127.0.0.1', 27016, 'mvp-open-risk', true);
            """);

        command.Parameters.AddWithValue("WorldId", worldId);
        command.Parameters.AddWithValue("DisplayName", worldId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ExecuteScalarIntAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
