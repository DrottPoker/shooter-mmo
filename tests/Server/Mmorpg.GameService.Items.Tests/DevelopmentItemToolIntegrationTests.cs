using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

public sealed class DevelopmentItemToolIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ListAndRepeatableCustomGrantUseCanonicalPersistence()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var service = CreateService(context);

        var list = await service.ExecuteAsync(
            new DevelopmentItemToolCommand(
                DevelopmentItemToolCommandKind.List,
                Guid.Empty,
                string.Empty,
                0,
                DevelopmentItemDestination.PermanentInventory,
                string.Empty),
            CancellationToken.None);

        Assert.True(list.Success);
        var listedCharacter = Assert.Single(list.Data!.Characters);
        Assert.Equal(player.Character.Id, listedCharacter.CharacterId);
        Assert.False(listedCharacter.IsOnline);
        Assert.Equal(0, listedCharacter.ItemCount);
        Assert.Contains(
            list.Data.Definitions,
            definition => definition.DefinitionId == "fixture.ore"
                && definition.MaximumStackSize == 100);
        Assert.Contains(
            list.Data.Packages,
            package => package.PackageId == "full_inventory"
                && package.RequiresEmptyCharacter);

        var command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Grant,
            player.Character.Id,
            "fixture.ore",
            20,
            DevelopmentItemDestination.Bank,
            string.Empty);
        await service.ExecuteAsync(command, CancellationToken.None);
        var secondGrant = await service.ExecuteAsync(command, CancellationToken.None);

        var refreshedCharacter = Assert.Single(secondGrant.Data!.Characters);
        Assert.Equal(2, refreshedCharacter.ItemCount);
        Assert.Equal(0, refreshedCharacter.CarriedWeight);
        var inventory = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(inventory.Succeeded, inventory.Error?.Message);
        Assert.Equal(2, inventory.Value!.Bank.Slots.Count(slot => slot.Item is not null));
        Assert.All(
            inventory.Value.Bank.Slots.Where(slot => slot.Item is not null),
            slot => Assert.Equal("fixture.ore", slot.Item!.DefinitionId));
    }

    [PostgresIntegrationFact]
    public async Task EncumbrancePackageReachesHardCapAndFurtherGrantIsRejected()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var service = CreateService(context);
        var packageCommand = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Package,
            player.Character.Id,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            "encumbrance_140");

        var response = await service.ExecuteAsync(packageCommand, CancellationToken.None);

        Assert.True(response.Success);
        var character = Assert.Single(response.Data!.Characters);
        Assert.Equal(280, character.CarriedWeight);
        Assert.Equal(200, character.CarryCapacity);
        var inventory = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(inventory.Succeeded, inventory.Error?.Message);
        Assert.Equal(14_000, inventory.Value!.LoadRatioBasisPoints);
        Assert.Equal(2_000, inventory.Value.MovementMultiplierBasisPoints);

        var hardCapError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(
                new DevelopmentItemToolCommand(
                    DevelopmentItemToolCommandKind.Grant,
                    player.Character.Id,
                    "fixture.potion",
                    1,
                    DevelopmentItemDestination.PermanentInventory,
                    string.Empty),
                CancellationToken.None));
        Assert.Contains(
            ItemTransactionErrorCodes.CarryWeightLimitExceeded,
            hardCapError.Message);

        var secondPackageError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(packageCommand, CancellationToken.None));
        Assert.Contains("require a character with no items", secondPackageError.Message);
    }

    [PostgresIntegrationFact]
    public async Task EveryFocusedPackageCreatesItsDocumentedDeterministicState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var service = CreateService(context);
        var expectations = new[]
        {
            new PackageExpectation("equipment", 5, 0, 71, 200),
            new PackageExpectation("stack_operations", 6, 0, 0, 200),
            new PackageExpectation("encumbrance_100", 2, 0, 200, 200),
            new PackageExpectation("secure_container", 4, 0, 21, 200),
            new PackageExpectation("recovery_delivery", 2, 1, 0, 200)
        };

        for (var index = 0; index < expectations.Length; index++)
        {
            var expectation = expectations[index];
            var player = await context.RegisterPlayerAsync(
                $"package-{index}@example.com",
                $"package_player_{index}",
                $"Package Hero {index}");
            var response = await service.ExecuteAsync(
                new DevelopmentItemToolCommand(
                    DevelopmentItemToolCommandKind.Package,
                    player.Character.Id,
                    string.Empty,
                    0,
                    DevelopmentItemDestination.PermanentInventory,
                    expectation.PackageId),
                CancellationToken.None);

            Assert.True(response.Success);
            var character = response.Data!.Characters.Single(candidate =>
                candidate.CharacterId == player.Character.Id);
            Assert.Equal(expectation.ItemCount, character.ItemCount);
            Assert.Equal(
                expectation.RecoveryDeliveryCount,
                character.RecoveryDeliveryCount);
            Assert.Equal(expectation.CarriedWeight, character.CarriedWeight);
            Assert.Equal(expectation.CarryCapacity, character.CarryCapacity);
        }
    }

    [PostgresIntegrationFact]
    public async Task LevelCommandSetsTheLevelWithinTheCatalogCap()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "level-tool@example.com",
            "level_tool_player",
            "Level Tool Hero");
        var service = CreateService(context);

        var response = await service.ExecuteAsync(
            LevelCommand(player.Character.Id, 7),
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        Assert.Equal(
            7,
            await connection.ExecuteScalarAsync<int>(
                "select level from characters where id = @CharacterId;",
                new { CharacterId = player.Character.Id }));
        var aboveCap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(LevelCommand(player.Character.Id, 201), CancellationToken.None));
        Assert.Contains("maximum level 200", aboveCap.Message, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task CurrencyCommandCreditsTheWalletWithADevelopmentLedgerRow()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "currency-tool@example.com",
            "currency_tool_player",
            "Currency Tool Hero");
        var service = CreateService(context);

        var response = await service.ExecuteAsync(
            CurrencyCommand(player.Character.Id, 250),
            CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.Equal(250, response.Data!.Characters.Single(candidate =>
            candidate.CharacterId == player.Character.Id).Currency);
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var ledger = (await connection.QueryAsync<(string SourceKind, long Delta, long BalanceAfter)>(
            """
            select source_kind as "SourceKind", delta as "Delta", balance_after as "BalanceAfter"
            from character_currency_ledger
            where character_id = @CharacterId
            order by id;
            """,
            new { CharacterId = player.Character.Id })).ToArray();
        Assert.Equal([("development", 250L, 250L)], ledger);
        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(CurrencyCommand(player.Character.Id, CurrencyRules.MaximumBalance), CancellationToken.None));
        Assert.Contains("maximum currency balance", overflow.Message, StringComparison.Ordinal);
    }

    [PostgresIntegrationFact]
    public async Task CorpseFixtureSeedsEmptyCharacterAndUsesDurableDeathPipeline()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "corpse-fixture@example.com",
            "corpse_fixture_player",
            "Corpse Fixture Hero");
        var service = CreateService(context);

        var response = await service.ExecuteAsync(
            new DevelopmentItemToolCommand(
                DevelopmentItemToolCommandKind.Corpse,
                player.Character.Id,
                string.Empty,
                0,
                DevelopmentItemDestination.PermanentInventory,
                string.Empty,
                "local-shard-1",
                12.5d,
                3d,
                -8.25d),
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Contains("Restart SimulationWorker", response.Message, StringComparison.Ordinal);
        var restored = await context.CorpseService.ListForWorkerAsync(
            "local-simulation-worker-1",
            "integration-worker-runtime",
            "local-shard-1",
            CancellationToken.None);
        Assert.True(restored.Succeeded, restored.Error?.Message);
        var corpse = Assert.Single(restored.Value!.Corpses);
        Assert.Equal(player.Character.Id, corpse.SourceCharacterId);
        Assert.Equal(12.5d, corpse.PositionX);
        Assert.Equal(3d, corpse.PositionY);
        Assert.Equal(-8.25d, corpse.PositionZ);
        Assert.Equal(TimeSpan.FromMinutes(5), corpse.ExpiresAt - corpse.CreatedAt);
        Assert.False(corpse.IsEmpty);
        Assert.Equal(
            new[] { "bag", "equipment", "general_inventory" },
            corpse.Sections.Select(section => section.SectionKind).Order().ToArray());
    }

    private static DevelopmentItemToolCommand LevelCommand(Guid characterId, int level)
    {
        return new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Level,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            string.Empty,
            Level: level);
    }

    private static DevelopmentItemToolCommand CurrencyCommand(Guid characterId, long amount)
    {
        return new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Currency,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            string.Empty,
            Amount: amount);
    }

    private static DevelopmentItemToolService CreateService(
        PostgresIntegrationTestContext context)
    {
        return new DevelopmentItemToolService(
            context.DataSource,
            context.ItemTransactionService,
            new DevelopmentItemPackageApplier(
                context.DataSource,
                context.ItemTransactionService,
                context.CatalogSource),
            context.Services.GetRequiredService<DevelopmentItemPackageSource>(),
            context.CorpseService,
            context.Services.GetRequiredService<ICharacterWallet>(),
            context.Services.GetRequiredService<ICharacterProgression>(),
            context.CatalogSource,
            new DevelopmentHostEnvironment());
    }

    private sealed class DevelopmentHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Mmorpg.GameService.Items.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record PackageExpectation(
        string PackageId,
        int ItemCount,
        int RecoveryDeliveryCount,
        long CarriedWeight,
        long CarryCapacity);
}
