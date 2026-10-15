using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService;
using Mmorpg.GameService.Config;
using Mmorpg.GameService.Database;
using Mmorpg.GameService.Database.Migrations;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Platform.Modules;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// The migration runner stores only ids, so an edited historic migration would never
// run again on existing databases. These hashes freeze the applied history.
public sealed class MigrationHistoryTests
{
    // Append new migrations here when they ship. Never edit an existing line.
    private static readonly string[] FrozenHistory =
    [
        "202607071600_auth_character_world_join d1eb62244850632328c7550c24c834736690f96569b40252a631ae554b0abc42",
        "202607101200_character_world_sessions 21b1f5cd1f1556d0542cbf7e49333a697f0f76700a3fbdcdbc49e851a6109132",
        "202607121200_session_ticket_ownership 9dd32fafc7a28a13fb771ad9b20b8454726c801dc71fd383243a48c6c14f97d6",
        "202607121500_world_session_ownership 2c6fd34acd51ff0e1817178f6781d108a33f6cb3fdd8ddb06a1558ebbe1fea95",
        "202607131000_world_registry_heartbeat 3063019da6e3b05fb456569f603a0ea6729ea8cd8b964a8d4586b9e761bee441",
        "202607131900_single_active_account_session 1fb93d4a8177ad3dd2f330baf9c15e06b08504dbbb51a1fc271bc9b57a0fe4e0",
        "202607132100_world_runtime_registration fc2efb8c2565b36844678cf5caa9a28d7824b80a8ef636c20fbc10a8d05fd5be",
        "202607141200_simulation_topology 4d488e883b860b10c98a03c2f7d8541bc83cd978399387e54f1de2ffd31683a4",
        "202607141300_single_active_account_simulation_session 3f883aedf503f6aa72df9977ffd10944450c87dd54e260e78e8d2cd62d25152f",
        "202607151200_item_persistence_foundation 849d5e9f59c2e7581ee649eeee0755d718c674a4858ea28f2d039b7974abd900",
        "202607171200_player_corpse_persistence c9bdd00c595ed32c2a5716ee872c724212bf5122997b824e0de8154a2c57ef4e",
        "202607171210_player_death_carry_overflow b599f789e0cb81dc62131d77e1f5531b1d3d9fcf747b4be7b991059f351548d8",
        "202607181800_item_operations_hardening 7513030620d9f3c3a9fb4fd7822c74124048f1702529c67d5cea671d182fd18e",
        "202607182100_development_world_identity 20958769fb34a9836274bcbe6adb96016685607e87037d9f91c8d49621255faf",
        "202607191200_worker_selected_world_binding 20f65dda3a97d4db94ed127fd14c1f80ee72319d93783caedbd7b5a6f5b0687c",
        "202610141800_release_retryable_operation_claims 0ecee7b2a4475cac259fc125da2f72abcdb12520f9f4feb97aad9f78ba49645d",
        "202610151000_equipped_item_lookup_index ffeb417381fc8724882fac85037dc70206a5adff3432cf9a14a6674362e70ee1"
    ];

    [Fact]
    public void HistoricMigrationsKeepTheirIdsOrderAndSql()
    {
        var actual = Migrations()
            .Take(FrozenHistory.Length)
            .Select(migration => $"{migration.Id} {Sha256(migration.Sql)}")
            .ToArray();

        Assert.Equal(
            string.Join(Environment.NewLine, FrozenHistory),
            string.Join(Environment.NewLine, actual));
    }

    [Fact]
    public void MigrationIdsAreUniqueAndOrdinallyAscending()
    {
        var ids = Migrations().Select(migration => migration.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [PostgresIntegrationFact]
    public async Task FreshDatabaseRecordsExactlyTheMigrationHistory()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedShard: false);

        await using var command = context.DataSource.CreateCommand(
            "select id from schema_migrations order by id;");
        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        Assert.Equal(Migrations().Select(migration => migration.Id), applied);
    }

    [Fact]
    public void SchemaRejectsDuplicateAndRewrittenHistory()
    {
        var duplicate = new TestModule(
            [new DatabaseMigration("202607151200_item_persistence_foundation", "select 1;")]);
        var beforeLastShipped = new TestModule(
            [new DatabaseMigration("202610141900_inserted_into_history", "select 1;")]);

        var duplicateError = Assert.Throws<InvalidOperationException>(
            () => GameServiceSchema.Create(
                LegacyBaselineMigrations.All,
                [.. ProductionModules(), duplicate]));
        var insertedError = Assert.Throws<InvalidOperationException>(
            () => GameServiceSchema.Create(
                LegacyBaselineMigrations.All,
                [.. ProductionModules(), beforeLastShipped]));

        Assert.Contains("defined more than once", duplicateError.Message, StringComparison.Ordinal);
        Assert.Contains("cannot change", insertedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaAcceptsMigrationsAfterTheShippedHistory()
    {
        var newer = new TestModule(
            [new DatabaseMigration("202610160000_newer_than_history", "select 1;")]);

        var schema = GameServiceSchema.Create(
            LegacyBaselineMigrations.All,
            [.. ProductionModules(), newer]);

        Assert.Equal("202610160000_newer_than_history", schema.Migrations[^1].Id);
    }

    private static IEnumerable<(string Id, string Sql)> Migrations()
    {
        return GameServiceComposition.CreateSchema(TestConfiguration())
            .Migrations
            .Select(migration => (migration.Id, migration.Sql));
    }

    private static IReadOnlyList<IGameServiceModule> ProductionModules()
    {
        return GameServiceComposition.CreateModules(TestConfiguration());
    }

    private static GameServiceConfiguration TestConfiguration()
    {
        return GameServiceTestComposition.CreateConfiguration(
            "Host=localhost;Database=migration_history_test;Username=test;Password=test");
    }

    private sealed class TestModule(IReadOnlyList<DatabaseMigration> migrations) : IGameServiceModule
    {
        public string Name => "Test";

        public IReadOnlyList<DatabaseMigration> Migrations { get; } = migrations;

        public void AddServices(IServiceCollection services)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
