using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Npgsql;

namespace Mmorpg.GameService.Items.Tests;

// An item that requires a level above the highest character level could never be used, so the
// Items seeder refuses the catalog at startup, before it reads the database.
public sealed class ItemCatalogLevelStartupTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // The fixture's helm requires level 3.
    private const string HelmId = "fixture.helm";

    [Fact]
    public async Task TheStartRefusesAnItemAboveTheHighestLevel()
    {
        await using var dataSource = UnreachableDataSource();
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var seeder = ActivatorUtilities.CreateInstance<ItemsDatabaseSeeder>(services, new FixedProgression(2));

        foreach (var start in new Func<Task>[] { () => seeder.PlanAsync(None), () => seeder.SeedAsync(None) })
        {
            var exception = await Assert.ThrowsAsync<ItemCatalogCompatibilityException>(start);
            Assert.Contains("highest level 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"{HelmId} requires level 3", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnItemAtTheHighestLevelPassesTheCheck()
    {
        await using var dataSource = UnreachableDataSource();
        await using var services = GameServiceTestComposition.BuildServiceProvider(dataSource);
        var seeder = ActivatorUtilities.CreateInstance<ItemsDatabaseSeeder>(services, new FixedProgression(3));

        // The check passes, so the plan goes on to the database, which does not exist.
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => seeder.PlanAsync(None));
    }

    private static NpgsqlDataSource UnreachableDataSource()
    {
        return NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
    }

    private sealed class FixedProgression(int maximumLevel) : ICharacterProgression
    {
        public int MaximumLevel => maximumLevel;

        public Task<CharacterProgressionSnapshot?> ReadAsync(
            PostgresTransactionScope scope,
            Guid characterId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CharacterExperienceChange?> GrantExperienceAsync(
            PostgresTransactionScope scope,
            Guid characterId,
            long experience,
            string source,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CharacterExperienceChange?> GrantKillExperienceAsync(
            PostgresTransactionScope scope,
            Guid characterId,
            int mobLevel,
            int mobExperiencePercent,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<bool> SetLevelAsync(
            PostgresTransactionScope scope,
            Guid characterId,
            int level,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
