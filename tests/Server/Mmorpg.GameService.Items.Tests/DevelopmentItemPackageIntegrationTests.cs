using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Items.Development;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

public sealed class DevelopmentItemPackageIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task FullInventoryPackageCreatesItsDeterministicStateOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var applier = new DevelopmentItemPackageApplier(
            context.DataSource,
            context.ItemTransactionService,
            context.CatalogSource);
        var package = LoadFixturePackage(context, "full_inventory");

        var result = await applier.ApplyAsync(package, player.Character.Id, CancellationToken.None);

        Assert.Equal(player.Character.Id, result.CharacterId);
        Assert.Equal(122, result.CarriedWeight);
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
        Assert.Equal(4880, snapshot.LoadRatioBasisPoints);
        Assert.Equal(10_000, snapshot.MovementMultiplierBasisPoints);

        Assert.NotNull(snapshot.EquippedBag);
        Assert.Equal("fixture.pack", snapshot.EquippedBag.Item.DefinitionId);
        Assert.Equal(
            "fixture.potion",
            snapshot.EquippedBag.Contents.Slots[4].Item!.DefinitionId);
        Assert.Equal(
            "fixture.ore",
            snapshot.EquippedBag.Contents.Slots[5].Item!.DefinitionId);
        Assert.Equal(
            "fixture.arrow",
            snapshot.EquippedBag.Contents.Slots[6].Item!.DefinitionId);

        Assert.Equal("fixture.ore", snapshot.Bank.Slots[0].Item!.DefinitionId);
        Assert.Equal(20, snapshot.Bank.Slots[0].Item!.Quantity);
        Assert.Equal(0, snapshot.Bank.Slots[0].Item!.Revision);
        Assert.Equal(16, snapshot.Bank.Slots[1].Item!.Quantity);
        Assert.Equal("fixture.potion", snapshot.Bank.Slots[2].Item!.DefinitionId);
        Assert.Equal("fixture.arrow", snapshot.Bank.Slots[3].Item!.DefinitionId);
        Assert.Equal("fixture.pack", snapshot.Bank.Slots[4].Item!.DefinitionId);
        Assert.Null(snapshot.Bank.Slots[10].Item);
        Assert.Null(snapshot.Bank.Slots[11].Item);

        Assert.Equal(
            "fixture.letter",
            snapshot.PermanentInventory.Slots[6].Item!.DefinitionId);
        var delivery = Assert.Single(snapshot.RecoveryStorage.Deliveries);
        Assert.True(delivery.Revision >= 0);
        Assert.Equal(DevelopmentItemPackageApplier.RecoverySourceKind, delivery.SourceKind);
        Assert.Equal(2, delivery.Items.Count);

        var secondRun = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            applier.ApplyAsync(package, player.Character.Id, CancellationToken.None));
        Assert.Contains("require a character with no items", secondRun.Message);
    }

    [PostgresIntegrationFact]
    public async Task PackageWithAStaleExpectedLoadIsRejected()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var applier = new DevelopmentItemPackageApplier(
            context.DataSource,
            context.ItemTransactionService,
            context.CatalogSource);
        var package = LoadFixturePackage(context, "encumbrance_100") with { ExpectedLoadPercent = 90 };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            applier.ApplyAsync(package, player.Character.Id, CancellationToken.None));

        Assert.Contains("instead of exactly 90 percent", error.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was granted", error.Message, StringComparison.Ordinal);

        // The character stays empty, so the corrected package can still be applied.
        var corrected = await applier.ApplyAsync(
            LoadFixturePackage(context, "encumbrance_100"),
            player.Character.Id,
            CancellationToken.None);
        Assert.Equal(corrected.CarryCapacity, corrected.CarriedWeight);
    }

    private static DevelopmentItemPackage LoadFixturePackage(
        PostgresIntegrationTestContext context,
        string packageId)
    {
        var source = context.Services.GetRequiredService<DevelopmentItemPackageSource>();
        return DevelopmentItemPackageLoader.Load(source.PackagesPath)
            .Packages
            .Single(package => package.Id == packageId);
    }
}
