using Mmorpg.GameService.Characters;
using Mmorpg.GameService.Platform.Audit;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The Items checks of the economy audit: a world with every kind of custody is clean, and each
// violation planted directly in the database is reported by exactly the check that owns it.
public sealed class EconomyAuditIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AWorldWithEveryKindOfCustodyIsClean()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var keeper = await world.RegisterAsync("audit_keeper");
        var keeperState = await world.LoadStateAsync(keeper.Character.Id);
        await world.GrantAsync(keeper.Character.Id, "fixture.ore", 5, keeperState.PermanentInventoryContainerId, 0);
        var arrows = await world.GrantAsync(keeper.Character.Id, "fixture.arrow", 20, keeperState.PermanentInventoryContainerId, 1);
        await world.EquipAsync(keeper.Character.Id, arrows, "ammunition");
        var pack = await world.GrantAsync(keeper.Character.Id, "fixture.pack", 1, keeperState.PermanentInventoryContainerId, 2);
        await world.EquipAsync(keeper.Character.Id, pack, "bag");
        var bagContents = (await world.LoadItemAsync(pack)).BagContentsContainerId!.Value;
        await world.GrantAsync(keeper.Character.Id, "fixture.potion", 3, bagContents, 4);
        await world.GrantAsync(keeper.Character.Id, "fixture.potion", 2, keeperState.BankContainerId, 0);
        var ring = await world.GrantAsync(keeper.Character.Id, "fixture.ring", 1, keeperState.PermanentInventoryContainerId, 3);
        await world.AddRecoveryDeliveryAsync(keeper.Character.Id, ring);

        // A death leaves a corpse with the inventory, the equipment, and the equipped Bag's contents.
        var fallen = await world.RegisterAsync("audit_fallen");
        var fallenState = await world.LoadStateAsync(fallen.Character.Id);
        await world.GrantAsync(fallen.Character.Id, "fixture.ore", 7, fallenState.PermanentInventoryContainerId, 0);
        var fallenArrows = await world.GrantAsync(fallen.Character.Id, "fixture.arrow", 10, fallenState.PermanentInventoryContainerId, 1);
        await world.EquipAsync(fallen.Character.Id, fallenArrows, "ammunition");
        var fallenPack = await world.GrantAsync(fallen.Character.Id, "fixture.pack", 1, fallenState.PermanentInventoryContainerId, 2);
        await world.EquipAsync(fallen.Character.Id, fallenPack, "bag");
        var fallenContents = (await world.LoadItemAsync(fallenPack)).BagContentsContainerId!.Value;
        await world.GrantAsync(fallen.Character.Id, "fixture.ore", 4, fallenContents, 0);
        await world.ProcessDeathAsync(fallen.Character.Id);

        var report = await EconomyAudit.RunAsync(context);

        Assert.True(report.Outcome == AuditOutcome.Clean, AuditReportWriter.ToText(report));
        Assert.Equal(ItemsAudit.Checks.Count + CharactersAudit.Checks.Count, report.Checks.Count);
        var custody = Assert.Single(report.Checks, check => check.Id == "item_custody_missing");
        Assert.Equal(
            (long)await context.ExecuteScalarIntAsync("select count(*) from item_instances;"),
            custody.Examined);
        Assert.True(custody.Examined >= 9);
        Assert.True(report.Checks.Single(check => check.Id == "item_journal_quantity_changed").Examined > 0);
    }

    [PostgresIntegrationFact]
    public async Task AnItemThatNoPlaceClaimsIsMissing()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var ore = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        var orphanage = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_containers (id, container_type, slot_capacity)
            values (@Container, 'corpse_inventory', 1);
            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@Container, 0, 'general');
            update item_instances
            set container_id = @Container,
                container_slot_index = 0
            where id = @Item;
            """,
            new { Container = orphanage, Item = ore });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_custody_missing");

        Assert.Equal(1, check.Violations);
        var example = Assert.Single(check.Examples);
        Assert.Equal(ore.ToString(), example["item_instance_id"]);
        Assert.Equal("container_without_owner", example["problem"]);
        Assert.Equal(orphanage.ToString(), example["container_id"]);
    }

    [PostgresIntegrationFact]
    public async Task AnItemLeftInAClosedContainerIsMissing()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        await world.ExecuteSqlAsync(
            "update item_containers set lifecycle = 'closed' where id = @Container;",
            new { Container = state.BankContainerId });
        await world.GrantAsync(player, "fixture.potion", 1, state.PermanentInventoryContainerId, 5);
        await world.ExecuteSqlAsync(
            """
            update item_instances
            set container_id = @Bank,
                container_slot_index = 0
            where container_id = @Inventory
              and container_slot_index = 5;
            """,
            new { Bank = state.BankContainerId, Inventory = state.PermanentInventoryContainerId });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_custody_missing");

        Assert.Equal("container_not_live", Assert.Single(check.Examples)["problem"]);
    }

    [PostgresIntegrationFact]
    public async Task ASecondCustodyRowPutsAnItemInTwoPlaces()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var ore = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        var delivery = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into recovery_deliveries (
                id,
                character_id,
                recovery_storage_container_id,
                source_kind,
                source_event_id)
            values (@Delivery, @Character, @Recovery, 'audit_test', 'planted');
            insert into recovery_delivery_items (recovery_delivery_id, item_instance_id, item_order)
            values (@Delivery, @Item, 0);
            """,
            new
            {
                Delivery = delivery,
                Character = player,
                Recovery = state.RecoveryStorageContainerId,
                Item = ore
            });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_custody_duplicated");

        var example = Assert.Single(check.Examples);
        Assert.Equal(ore.ToString(), example["item_instance_id"]);
        Assert.Equal("recovery_delivery_names_item_elsewhere", example["problem"]);
        Assert.Equal(delivery.ToString(), example["recovery_delivery_id"]);
    }

    [PostgresIntegrationFact]
    public async Task AContainerThatACorpseAndACharacterBothNameHoldsItsItemsTwice()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var fallen = await world.RegisterAsync("audit_empty_fallen");
        var corpse = await world.ProcessDeathAsync(fallen.Character.Id);
        await world.ExecuteSqlAsync(
            """
            update corpse_sections
            set container_id = @Inventory
            where corpse_id = @Corpse
              and section_kind = 'general_inventory';
            """,
            new { Inventory = state.PermanentInventoryContainerId, Corpse = corpse });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_custody_duplicated");

        Assert.Equal("container_with_two_owners", Assert.Single(check.Examples)["problem"]);
    }

    [PostgresIntegrationFact]
    public async Task AStackOverItsLimitIsOutOfRange()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var ore = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        await world.ExecuteSqlAsync(
            "update item_instances set quantity = 101 where id = @Item;",
            new { Item = ore });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_quantity_out_of_range");

        var example = Assert.Single(check.Examples);
        Assert.Equal("101", example["quantity"]);
        Assert.Equal("100", example["maximum_stack_size"]);
    }

    [PostgresIntegrationFact]
    public async Task AnOperationPendingLongerThanTheSettingIsReported()
    {
        var (context, world, _) = await PlayerWithOreAsync();
        await using var __ = context;
        var stale = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_operations (
                operation_id,
                operation_kind,
                request_hash,
                status,
                request_payload,
                created_at)
            values
                (@Stale, 'relocate', repeat('a', 64), 'pending', '{}'::jsonb, now() - interval '1 hour'),
                (@Fresh, 'relocate', repeat('b', 64), 'pending', '{}'::jsonb, now());
            """,
            new { Stale = stale, Fresh = Guid.NewGuid() });

        var check = await EconomyAudit.AssertOnlyViolationAsync(
            context,
            "item_operation_pending_too_long",
            AuditOptions.Default);

        Assert.Equal(1, check.Violations);
        Assert.Equal(stale.ToString(), Assert.Single(check.Examples)["operation_id"]);
    }

    [PostgresIntegrationFact]
    public async Task TwoItemsInOneSlotShareIt()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        await world.GrantAsync(player, "fixture.potion", 1, state.PermanentInventoryContainerId, 1);
        // The unique constraint keeps this from happening; the check guards a schema without it.
        await world.ExecuteSqlAsync(
            """
            alter table item_instances drop constraint ux_item_instances_container_slot;
            update item_instances
            set container_slot_index = 0
            where container_id = @Inventory
              and container_slot_index = 1;
            """,
            new { Inventory = state.PermanentInventoryContainerId });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_slot_shared");

        var example = Assert.Single(check.Examples);
        Assert.Equal("container_slot", example["slot_kind"]);
        Assert.Equal(state.PermanentInventoryContainerId.ToString(), example["holder_id"]);
        Assert.Equal("2", example["item_count"]);
    }

    [PostgresIntegrationFact]
    public async Task ASlotBeyondItsContainerIsInvalid()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        await world.ExecuteSqlAsync(
            """
            insert into item_container_slots (container_id, slot_index, slot_kind)
            values (@Inventory, 25, 'general');
            update item_instances
            set container_slot_index = 25
            where container_id = @Inventory
              and container_slot_index = 0;
            """,
            new { Inventory = state.PermanentInventoryContainerId });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_slot_invalid");

        var example = Assert.Single(check.Examples);
        Assert.Equal("slot_outside_container", example["problem"]);
        Assert.Equal("25", example["container_slot_index"]);
    }

    [PostgresIntegrationFact]
    public async Task ADefinitionThatLeftTheCatalogIsUnknown()
    {
        var (context, world, _) = await PlayerWithOreAsync();
        await using var __ = context;
        await world.ExecuteSqlAsync("update item_definitions set is_active = false where id = 'fixture.ore';");

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_definition_unknown");

        Assert.Equal("fixture.ore", Assert.Single(check.Examples)["definition_id"]);
    }

    [PostgresIntegrationFact]
    public async Task ADestroyedItemThatStillExistsIsReported()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var ore = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        await world.ExecuteSqlAsync(
            """
            insert into item_destructions (
                item_instance_id,
                definition_id,
                source_operation_id,
                quantity,
                reason)
            values (@Item, 'fixture.ore', gen_random_uuid(), 5, 'audit_test');
            """,
            new { Item = ore });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_destroyed_but_present");

        Assert.Equal(ore.ToString(), Assert.Single(check.Examples)["item_instance_id"]);
    }

    [PostgresIntegrationFact]
    public async Task AnItemThatTheJournalRemovedButStillExistsIsReported()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var state = await world.LoadStateAsync(player);
        var ore = await world.FindItemAsync(state.PermanentInventoryContainerId, 0);
        var operation = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_operations (
                operation_id,
                operation_kind,
                request_hash,
                status,
                request_payload,
                result_payload,
                completed_at)
            values (@Operation, 'destroy', repeat('c', 64), 'committed', '{}'::jsonb, '{}'::jsonb, now());
            insert into item_operation_changes (
                operation_id,
                change_index,
                item_instance_id,
                change_kind,
                before_state,
                after_state)
            values (
                @Operation,
                0,
                @Item,
                'item_destroyed',
                jsonb_build_object('itemInstanceId', @Item, 'definitionId', 'fixture.ore', 'quantity', 5),
                null);
            """,
            new { Operation = operation, Item = ore });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_journal_removed_but_present");

        var example = Assert.Single(check.Examples);
        Assert.Equal(ore.ToString(), example["item_instance_id"]);
        Assert.Equal(operation.ToString(), example["operation_id"]);
    }

    [PostgresIntegrationFact]
    public async Task AMoveWhoseJournalGainsQuantityBreaksConservation()
    {
        var (context, world, _) = await PlayerWithOreAsync();
        await using var __ = context;
        var operation = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_operations (
                operation_id,
                operation_kind,
                request_hash,
                status,
                request_payload,
                result_payload,
                completed_at)
            values (@Operation, 'merge_stacks', repeat('d', 64), 'committed', '{}'::jsonb, '{}'::jsonb, now());
            insert into item_operation_changes (
                operation_id,
                change_index,
                item_instance_id,
                change_kind,
                before_state,
                after_state)
            values (
                @Operation,
                0,
                gen_random_uuid(),
                'stack_merge_target',
                jsonb_build_object('definitionId', 'fixture.ore', 'quantity', 1),
                jsonb_build_object('definitionId', 'fixture.ore', 'quantity', 5));
            """,
            new { Operation = operation });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "item_journal_quantity_changed");

        var example = Assert.Single(check.Examples);
        Assert.Equal(operation.ToString(), example["operation_id"]);
        Assert.Equal("merge_stacks", example["operation_kind"]);
        Assert.Equal("4", example["quantity_change"]);
    }

    [PostgresIntegrationFact]
    public async Task AGoldChangeOfARejectedOperationIsReported()
    {
        var (context, world, player) = await PlayerWithOreAsync();
        await using var _ = context;
        var operation = Guid.NewGuid();
        await world.ExecuteSqlAsync(
            """
            insert into item_operations (
                operation_id,
                actor_character_id,
                operation_kind,
                request_hash,
                status,
                request_payload,
                result_payload,
                completed_at)
            values (@Operation, @Character, 'vendor_sale', repeat('e', 64), 'rejected', '{}'::jsonb, '{}'::jsonb, now());
            update characters set currency = currency + 10 where id = @Character;
            insert into character_currency_ledger (
                character_id,
                source_kind,
                source_id,
                item_operation_id,
                delta,
                balance_after)
            select @Character, 'vendor_sale', 'fixture.vendor', @Operation, 10, currency
            from characters
            where id = @Character;
            """,
            new { Operation = operation, Character = player });

        var check = await EconomyAudit.AssertOnlyViolationAsync(context, "currency_change_operation_committed");

        var example = Assert.Single(check.Examples);
        Assert.Equal(operation.ToString(), example["item_operation_id"]);
        Assert.Equal("rejected", example["status"]);
    }

    // A fresh character with five ore in the first inventory slot, audited clean.
    private static async Task<(PostgresIntegrationTestContext Context, ItemScenarioWorld World, Guid Player)> PlayerWithOreAsync()
    {
        var context = await PostgresIntegrationTestContext.CreateAsync();
        try
        {
            var world = new ItemScenarioWorld(context);
            var player = await world.RegisterAsync("audit_player");
            var state = await world.LoadStateAsync(player.Character.Id);
            await world.GrantAsync(player.Character.Id, "fixture.ore", 5, state.PermanentInventoryContainerId, 0);
            await EconomyAudit.AssertCleanAsync(context);
            return (context, world, player.Character.Id);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
