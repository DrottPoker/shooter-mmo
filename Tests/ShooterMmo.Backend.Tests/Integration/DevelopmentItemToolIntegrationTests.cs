using AuthService.Items;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ShooterMmo.Backend.Tests.Integration;

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
            definition => definition.DefinitionId == "material.iron_ore"
                && definition.MaximumStackSize == 100);
        Assert.Contains(
            list.Data.Packages,
            package => package.PackageId == "phase9_full"
                && package.RequiresEmptyCharacter);

        var command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Grant,
            player.Character.Id,
            "material.iron_ore",
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
            slot => Assert.Equal("material.iron_ore", slot.Item!.DefinitionId));
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
        Assert.False(inventory.Value.SprintEligible);
        Assert.Equal(2_000, inventory.Value.MovementMultiplierBasisPoints);

        var hardCapError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(
                new DevelopmentItemToolCommand(
                    DevelopmentItemToolCommandKind.Grant,
                    player.Character.Id,
                    "medical.field_dressing",
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

    private static DevelopmentItemToolService CreateService(
        PostgresIntegrationTestContext context)
    {
        var environment = new DevelopmentHostEnvironment();
        var phaseNineSeeder = new PhaseNineDevelopmentFixtureSeeder(
            context.DataSource,
            context.ItemTransactionService,
            context.ItemQueryService,
            environment);
        return new DevelopmentItemToolService(
            context.DataSource,
            context.ItemTransactionService,
            phaseNineSeeder,
            context.CorpseService,
            environment);
    }

    private sealed class DevelopmentHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ShooterMmo.Backend.Tests";

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
