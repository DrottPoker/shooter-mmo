using AuthService.Auth;
using AuthService.Characters;
using AuthService.Database;
using AuthService.Worlds;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class AuthWorldFlowIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task RegistrationCharacterAndWorldJoinFlowPersistsAcrossServices()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            PostgresIntegrationFactAttribute.ConnectionStringVariable)!;

        EnsureDedicatedTestDatabase(connectionString);

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await ResetPublicSchemaAsync(dataSource);

        var initializer = new DatabaseInitializer(
            dataSource,
            NullLogger<DatabaseInitializer>.Instance);

        await initializer.InitializeAsync(CancellationToken.None);
        await AssertFoundationSchemaAsync(dataSource);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:SessionLifetimeHours"] = "24",
                ["Game:MaxCharactersPerAccount"] = "5",
                ["WorldJoin:TicketLifetimeSeconds"] = "30"
            })
            .Build();

        var sessionService = new SessionService(dataSource, configuration);
        var accountService = new AccountService(dataSource, sessionService);
        var characterService = new CharacterService(dataSource, configuration);
        var worldService = new WorldService(dataSource, configuration);

        var registration = await accountService.RegisterAsync(
            new RegisterAccountRequest("integration@example.com", "integration_player", "TestPass123!"),
            CancellationToken.None);

        Assert.True(registration.Succeeded, registration.Error?.Message);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {registration.Value!.SessionToken}";

        var authenticatedAccount = await sessionService.AuthenticateAsync(
            httpContext.Request,
            CancellationToken.None);

        Assert.True(authenticatedAccount.Succeeded, authenticatedAccount.Error?.Message);
        Assert.Equal(registration.Value.AccountId, authenticatedAccount.Value!.AccountId);

        var character = await characterService.CreateAsync(
            registration.Value.AccountId,
            new CreateCharacterRequest("Integration Hero"),
            CancellationToken.None);

        Assert.True(character.Succeeded, character.Error?.Message);

        var characters = await characterService.ListAsync(
            registration.Value.AccountId,
            CancellationToken.None);

        Assert.Single(characters.Value!);
        Assert.Equal(character.Value!.Id, characters.Value!.Single().Id);

        var join = await worldService.CreateJoinTicketAsync(
            authenticatedAccount.Value,
            "local-world-1",
            new JoinWorldRequest(character.Value.Id),
            CancellationToken.None);

        Assert.True(join.Succeeded, join.Error?.Message);

        var consumed = await worldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(join.Value!.JoinTicket),
            CancellationToken.None);

        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(character.Value.Id, consumed.Value!.CharacterId);
        Assert.Equal("Integration Hero", consumed.Value.CharacterName);
        Assert.Equal("local-world-1", consumed.Value.WorldId);

        var consumedAgain = await worldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(join.Value.JoinTicket),
            CancellationToken.None);

        Assert.False(consumedAgain.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, consumedAgain.StatusCode);
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

    private static async Task AssertFoundationSchemaAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            select count(*)
            from information_schema.tables
            where table_schema = 'public'
              and table_name in (
                  'accounts',
                  'account_sessions',
                  'characters',
                  'worlds',
                  'world_join_tickets',
                  'schema_migrations');
            """);

        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(6, tableCount);
    }
}
