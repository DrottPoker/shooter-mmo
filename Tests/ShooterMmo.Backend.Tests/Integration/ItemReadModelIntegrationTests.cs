using System.Text.Json;
using AuthService.Items;
using Microsoft.AspNetCore.Http;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class ItemReadModelIntegrationTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [PostgresIntegrationFact]
    public async Task CatalogQueryReturnsOneOrderedCurrentDefinitionGraph()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();

        var result = await context.ItemCatalogQueryService.GetCurrentAsync(
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var catalog = result.Value!;
        var runtime = await ItemCatalogRuntimeLoader.LoadAsync(
            context.CatalogSource.RuntimeCatalogPath,
            CancellationToken.None);
        Assert.Equal(runtime.CatalogId, catalog.CatalogId);
        Assert.Equal(runtime.Revision, catalog.Revision);
        Assert.Equal(runtime.FormatVersion, catalog.FormatVersion);
        Assert.Equal(runtime.BaseSecureContainerTierId, catalog.BaseSecureContainerTierId);
        Assert.Equal(runtime.Definitions.Length, catalog.Definitions.Count);
        Assert.Equal(
            catalog.Definitions.Count,
            catalog.Definitions.Select(definition => definition.Id).Distinct().Count());
        Assert.Equal(
            catalog.Definitions.Select(definition => definition.Id).Order(),
            catalog.Definitions.Select(definition => definition.Id));

        var bag = Assert.Single(catalog.Definitions, definition => definition.Id == "bag.field_pack");
        Assert.NotNull(bag.Bag);
        Assert.Equal(50, bag.Bag.CarryCapacityBonus);
        Assert.Equal(Enumerable.Range(0, 7), bag.Bag.Slots.Select(slot => slot.SlotIndex));
        Assert.Equal(["ammunition"], bag.Bag.Slots[6].AcceptedTags);
        Assert.Equal(
            "secure_container.base",
            Assert.Single(catalog.SecureContainerTiers).Id);
    }

    [PostgresIntegrationFact]
    public async Task AnotherAccountCannotReadCharacterInventory()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var owner = await context.RegisterPlayerAsync();
        var other = await context.RegisterPlayerAsync(
            "other@example.com",
            "other_player",
            "Other Hero");

        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            other.Registration.AccountId,
            owner.Character.Id,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Equal("character_inventory_not_found", result.Error!.Code);
        Assert.Null(result.Value);
    }

    [PostgresIntegrationFact]
    public async Task NewCharacterReturnsEmptyCompleteAndStableOrderedState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Error?.Message);
        var snapshot = result.Value!;
        Assert.Equal(player.Character.Id, snapshot.CharacterId);
        Assert.Equal(0, snapshot.ItemStateRevision);
        Assert.Equal(0, snapshot.CarriedWeight);
        Assert.Equal(200, snapshot.CarryCapacity);
        Assert.Equal(0, snapshot.LoadRatioBasisPoints);
        Assert.True(snapshot.SprintEligible);
        Assert.Equal(10_000, snapshot.MovementMultiplierBasisPoints);

        AssertContainerIsEmptyAndOrdered(snapshot.PermanentInventory, "permanent_inventory", 20);
        AssertContainerIsEmptyAndOrdered(snapshot.Bank, "bank", 40);
        AssertContainerIsEmptyAndOrdered(snapshot.SecureContainer.Contents, "secure_container", 4);
        Assert.Equal("secure_container.base", snapshot.SecureContainer.TierId);
        Assert.Equal(Enumerable.Range(0, 8), snapshot.Equipment.Select(slot => slot.SortOrder));
        Assert.All(snapshot.Equipment, slot => Assert.Null(slot.Item));
        Assert.Null(snapshot.EquippedBag);
        Assert.Empty(snapshot.RecoveryStorage.Deliveries);
    }

    [PostgresIntegrationFact]
    public async Task OwnedSnapshotReturnsAllSectionsPoliciesAndDerivedEncumbrance()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var fixture = new ItemReadModelTestFixture(context.DataSource, player.Character.Id);
        var insuredRifleId = await fixture.AddContainerItemAsync(
            "permanent_inventory",
            0,
            "weapon.training_rifle",
            1,
            new TestItemPolicy("insured", "policy-owner-only"));
        var armorId = await fixture.EquipItemAsync(
            "body_armor",
            "armor.starter_vest");
        var bag = await fixture.EquipBagAsync();
        var bagAmmunitionId = await fixture.AddBagContentAsync(
            bag.ContainerId,
            6,
            "ammunition.training_556",
            30);
        var bankItemId = await fixture.AddContainerItemAsync(
            "bank",
            0,
            "material.iron_ore",
            5);
        var secureItemId = await fixture.AddContainerItemAsync(
            "secure_container",
            0,
            "medical.field_dressing",
            2);
        var recovery = await fixture.AddRecoveryDeliveryAsync(
            "test_recovery",
            "internal-event-id",
            "quest_item.signal_transponder",
            1,
            new TestItemPolicy("protected_on_death", "recovery-policy-source"));
        await fixture.SetCarryStateAsync(210, 200, 7);

        var catalogResult = await context.ItemCatalogQueryService.GetCurrentAsync(
            CancellationToken.None);
        var snapshotResult = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);

        Assert.True(catalogResult.Succeeded, catalogResult.Error?.Message);
        Assert.True(snapshotResult.Succeeded, snapshotResult.Error?.Message);
        var catalog = catalogResult.Value!;
        var snapshot = snapshotResult.Value!;
        Assert.Equal(catalog.Revision, snapshot.CatalogRevision);
        Assert.Equal(7, snapshot.ItemStateRevision);
        Assert.Equal(210, snapshot.CarriedWeight);
        Assert.Equal(200, snapshot.CarryCapacity);
        Assert.Equal(10_500, snapshot.LoadRatioBasisPoints);
        Assert.False(snapshot.SprintEligible);
        Assert.Equal(9_000, snapshot.MovementMultiplierBasisPoints);

        var rifle = snapshot.PermanentInventory.Slots[0].Item;
        Assert.NotNull(rifle);
        Assert.Equal(insuredRifleId, rifle.ItemInstanceId);
        Assert.Equal("weapon.training_rifle", rifle.DefinitionId);
        var policy = Assert.Single(rifle.Policies);
        Assert.Equal("insured", policy.PolicyKind);
        Assert.Equal("active", policy.Status);
        Assert.Equal(
            armorId,
            Assert.Single(snapshot.Equipment, slot => slot.EquipmentSlotId == "body_armor")
                .Item!
                .ItemInstanceId);

        Assert.NotNull(snapshot.EquippedBag);
        Assert.Equal(bag.ItemInstanceId, snapshot.EquippedBag.Item.ItemInstanceId);
        Assert.Equal(7, snapshot.EquippedBag.Contents.Slots.Count);
        Assert.Equal(
            bagAmmunitionId,
            snapshot.EquippedBag.Contents.Slots[6].Item!.ItemInstanceId);
        Assert.Equal(["ammunition"], snapshot.EquippedBag.Contents.Slots[6].AcceptedTags);
        Assert.Equal(bankItemId, snapshot.Bank.Slots[0].Item!.ItemInstanceId);
        Assert.Equal(
            secureItemId,
            snapshot.SecureContainer.Contents.Slots[0].Item!.ItemInstanceId);

        var delivery = Assert.Single(snapshot.RecoveryStorage.Deliveries);
        Assert.Equal(recovery.DeliveryId, delivery.DeliveryId);
        Assert.Equal("test_recovery", delivery.SourceKind);
        var deliveredItem = Assert.Single(delivery.Items);
        Assert.Equal(recovery.ItemInstanceId, deliveredItem.Item.ItemInstanceId);
        Assert.Equal(recovery.SlotIndex, deliveredItem.ContainerSlotIndex);
        Assert.Equal("protected_on_death", Assert.Single(deliveredItem.Item.Policies).PolicyKind);

        var definitionsById = catalog.Definitions.ToDictionary(
            definition => definition.Id,
            StringComparer.Ordinal);
        Assert.All(
            EnumerateItems(snapshot),
            item => Assert.True(definitionsById.ContainsKey(item.DefinitionId), item.DefinitionId));
    }

    [PostgresIntegrationFact]
    public async Task RepeatedDefinitionInstancesDoNotDuplicateCatalogMetadataInSnapshot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var fixture = new ItemReadModelTestFixture(context.DataSource, player.Character.Id);
        await fixture.AddContainerItemAsync(
            "permanent_inventory",
            0,
            "ammunition.training_556",
            20);
        await fixture.AddContainerItemAsync(
            "bank",
            0,
            "ammunition.training_556",
            40);

        var catalogResult = await context.ItemCatalogQueryService.GetCurrentAsync(
            CancellationToken.None);
        var snapshotResult = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);

        var catalog = catalogResult.Value!;
        var snapshot = snapshotResult.Value!;
        Assert.Equal(
            2,
            EnumerateItems(snapshot).Count(item =>
                item.DefinitionId == "ammunition.training_556"));
        Assert.Single(
            catalog.Definitions,
            definition => definition.Id == "ammunition.training_556");

        var json = JsonSerializer.Serialize(snapshot, WebJsonOptions);
        Assert.Equal(2, CountOccurrences(json, "ammunition.training_556"));
        AssertNotContains(json, "\"definitions\"");
        AssertNotContains(json, "displayName");
        AssertNotContains(json, "unitWeight");
        AssertNotContains(json, "iconResourcePath");
    }

    [PostgresIntegrationFact]
    public async Task PlayerDtosExcludeSecretsPolicySourcesAndOperationMetadata()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var fixture = new ItemReadModelTestFixture(context.DataSource, player.Character.Id);
        const string policySecret = "policy-source-must-not-leak";
        const string operationSecret = "operation-payload-must-not-leak";
        const string recoveryEventSecret = "recovery-event-must-not-leak";
        await fixture.AddContainerItemAsync(
            "permanent_inventory",
            0,
            "weapon.training_rifle",
            1,
            new TestItemPolicy("insured", policySecret));
        await fixture.AddRecoveryDeliveryAsync(
            "test_recovery",
            recoveryEventSecret,
            "medical.field_dressing");
        await fixture.AddOperationSentinelAsync(operationSecret);

        var catalog = (await context.ItemCatalogQueryService.GetCurrentAsync(
            CancellationToken.None)).Value!;
        var snapshot = (await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None)).Value!;
        var json = JsonSerializer.Serialize(new { Catalog = catalog, Snapshot = snapshot }, WebJsonOptions);

        AssertNotContains(json, policySecret);
        AssertNotContains(json, operationSecret);
        AssertNotContains(json, recoveryEventSecret);
        AssertNotContains(json, "sourceId");
        AssertNotContains(json, "sourceEventId");
        AssertNotContains(json, "requestHash");
        AssertNotContains(json, "requestPayload");
        AssertNotContains(json, "resultPayload");
        AssertNotContains(json, "structuralFingerprint");
        AssertNotContains(json, "sessionToken");
        AssertNotContains(json, "passwordHash");
    }

    [PostgresIntegrationFact]
    public async Task RepeatedReadQueriesDoNotMutateDurableState()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var fixture = new ItemReadModelTestFixture(context.DataSource, player.Character.Id);
        await fixture.AddContainerItemAsync(
            "permanent_inventory",
            0,
            "medical.field_dressing",
            2);
        var before = await LoadDurableReadChecksumAsync(context, player.Character.Id);

        for (var index = 0; index < 3; index++)
        {
            Assert.True((await context.ItemCatalogQueryService.GetCurrentAsync(
                CancellationToken.None)).Succeeded);
            Assert.True((await context.ItemQueryService.GetCharacterInventoryAsync(
                player.Registration.AccountId,
                player.Character.Id,
                CancellationToken.None)).Succeeded);
        }

        var after = await LoadDurableReadChecksumAsync(context, player.Character.Id);
        Assert.Equal(before, after);
    }

    private static void AssertContainerIsEmptyAndOrdered(
        ItemContainerSnapshotResponse container,
        string expectedType,
        int expectedCapacity)
    {
        Assert.Equal(expectedType, container.ContainerType);
        Assert.Equal(expectedCapacity, container.SlotCapacity);
        Assert.Equal(
            Enumerable.Range(0, expectedCapacity),
            container.Slots.Select(slot => slot.SlotIndex));
        Assert.All(container.Slots, slot => Assert.Null(slot.Item));
    }

    private static IEnumerable<ItemInstanceSnapshotResponse> EnumerateItems(
        CharacterInventorySnapshotResponse snapshot)
    {
        return snapshot.PermanentInventory.Slots
            .Concat(snapshot.Bank.Slots)
            .Concat(snapshot.SecureContainer.Contents.Slots)
            .Select(slot => slot.Item)
            .Concat(snapshot.Equipment.Select(slot => slot.Item))
            .Concat(snapshot.EquippedBag?.Contents.Slots.Select(slot => slot.Item)
                ?? Enumerable.Empty<ItemInstanceSnapshotResponse?>())
            .Concat(snapshot.RecoveryStorage.Deliveries
                .SelectMany(delivery => delivery.Items)
                .Select(item => item.Item))
            .Where(item => item is not null)
            .Select(item => item!);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static void AssertNotContains(string source, string value)
    {
        Assert.False(
            source.Contains(value, StringComparison.OrdinalIgnoreCase),
            $"Player DTO JSON unexpectedly contained '{value}'.");
    }

    private static async Task<DurableReadChecksum> LoadDurableReadChecksumAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var command = context.DataSource.CreateCommand(
            """
            select
                state.revision,
                (select count(*) from item_instances),
                (select count(*) from item_instance_policies),
                (select count(*) from item_operations),
                (select count(*) from recovery_deliveries)
            from character_item_states state
            where state.character_id = $1;
            """);
        command.Parameters.AddWithValue(characterId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DurableReadChecksum(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private sealed record DurableReadChecksum(
        long ItemStateRevision,
        long ItemCount,
        long PolicyCount,
        long OperationCount,
        long RecoveryDeliveryCount);
}
