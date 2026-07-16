using AuthService.Items;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class PhaseNineDevelopmentFixtureIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task SeederCreatesCompleteDeterministicPhaseNineFixtureOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var seeder = new PhaseNineDevelopmentFixtureSeeder(
            context.DataSource,
            context.ItemTransactionService,
            context.ItemQueryService,
            new DevelopmentHostEnvironment());

        var result = await seeder.SeedAsync(
            player.Character.Id,
            CancellationToken.None);

        Assert.Equal(player.Character.Id, result.CharacterId);
        Assert.Equal(132, result.CarriedWeight);
        Assert.Equal(250, result.CarryCapacity);
        Assert.Equal(17, result.GrantedItemCount);
        Assert.Equal(1, result.RecoveryDeliveryCount);

        var readResult = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(readResult.Succeeded, readResult.Error?.Message);
        var snapshot = readResult.Value!;
        Assert.Equal(result.ItemStateRevision, snapshot.ItemStateRevision);
        Assert.Equal(5280, snapshot.LoadRatioBasisPoints);
        Assert.True(snapshot.SprintEligible);
        Assert.Equal(10_000, snapshot.MovementMultiplierBasisPoints);

        Assert.NotNull(snapshot.EquippedBag);
        Assert.Equal("bag.field_pack", snapshot.EquippedBag.Item.DefinitionId);
        Assert.Equal(
            "medical.field_dressing",
            snapshot.EquippedBag.Contents.Slots[4].Item!.DefinitionId);
        Assert.Equal(
            "material.iron_ore",
            snapshot.EquippedBag.Contents.Slots[5].Item!.DefinitionId);
        Assert.Equal(
            "ammunition.training_556",
            snapshot.EquippedBag.Contents.Slots[6].Item!.DefinitionId);

        Assert.Equal("material.iron_ore", snapshot.Bank.Slots[0].Item!.DefinitionId);
        Assert.Equal(20, snapshot.Bank.Slots[0].Item!.Quantity);
        Assert.Equal(0, snapshot.Bank.Slots[0].Item!.Revision);
        Assert.Equal(16, snapshot.Bank.Slots[1].Item!.Quantity);
        Assert.Equal("medical.field_dressing", snapshot.Bank.Slots[2].Item!.DefinitionId);
        Assert.Equal("ammunition.training_556", snapshot.Bank.Slots[3].Item!.DefinitionId);
        Assert.Equal("bag.field_pack", snapshot.Bank.Slots[4].Item!.DefinitionId);
        Assert.Null(snapshot.Bank.Slots[10].Item);
        Assert.Null(snapshot.Bank.Slots[11].Item);

        Assert.Equal(
            "quest_item.signal_transponder",
            snapshot.SecureContainer.Contents.Slots[0].Item!.DefinitionId);
        var delivery = Assert.Single(snapshot.RecoveryStorage.Deliveries);
        Assert.True(delivery.Revision >= 0);
        Assert.Equal("development_phase9_fixture", delivery.SourceKind);
        Assert.Equal(2, delivery.Items.Count);

        var secondRun = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            seeder.SeedAsync(player.Character.Id, CancellationToken.None));
        Assert.Contains("requires a new character", secondRun.Message);
    }

    private sealed class DevelopmentHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "ShooterMmo.Backend.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
