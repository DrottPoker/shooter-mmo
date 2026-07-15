using System.Data;
using AuthService.Http;
using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed class ItemCatalogQueryService(NpgsqlDataSource dataSource)
{
    public async Task<ServiceResult<ItemCatalogResponse>> GetCurrentAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "set transaction read only;",
                transaction: transaction,
                cancellationToken: cancellationToken));

            var catalog = await connection.QuerySingleOrDefaultAsync<CatalogRow>(
                new CommandDefinition(
                    """
                    select
                        revision.catalog_id as "CatalogId",
                        revision.revision as "Revision",
                        revision.format_version as "FormatVersion",
                        revision.base_secure_container_tier_id as "BaseSecureContainerTierId"
                    from item_system_settings settings
                    join item_catalog_revisions revision
                      on revision.catalog_id = settings.catalog_id
                     and revision.is_current
                    where settings.id = 'character_default';
                    """,
                    transaction: transaction,
                    cancellationToken: cancellationToken));

            if (catalog is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return ServiceResult<ItemCatalogResponse>.NotFound(
                    "item_catalog_not_found",
                    "The current item catalog was not found.");
            }

            using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
                """
                select id as "Id", display_name as "DisplayName"
                from item_categories
                where catalog_id = @CatalogId
                  and catalog_revision = @Revision
                  and is_active
                order by id;

                select id as "Id", display_name as "DisplayName"
                from item_tags
                where catalog_id = @CatalogId
                  and catalog_revision = @Revision
                  and is_active
                order by id;

                select id as "Id", display_name as "DisplayName", sort_order as "SortOrder"
                from equipment_slots
                where catalog_id = @CatalogId
                  and catalog_revision = @Revision
                order by sort_order, id;

                select
                    id as "Id",
                    display_name as "DisplayName",
                    category_id as "CategoryId",
                    unit_weight as "UnitWeight",
                    maximum_stack_size as "MaximumStackSize",
                    player_destroyable as "PlayerDestroyable"
                from item_definitions
                where catalog_id = @CatalogId
                  and catalog_revision = @Revision
                  and is_active
                order by id;

                select relation.definition_id as "DefinitionId", relation.tag_id as "Value"
                from item_definition_tags relation
                join item_definitions definition on definition.id = relation.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by relation.definition_id, relation.tag_id;

                select
                    relation.definition_id as "DefinitionId",
                    relation.equipment_slot_id as "Value"
                from item_definition_equipment_slots relation
                join item_definitions definition on definition.id = relation.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by relation.definition_id, relation.equipment_slot_id;

                select
                    relation.definition_id as "DefinitionId",
                    relation.is_allowed as "IsAllowed"
                from item_definition_location_rules relation
                join item_definitions definition on definition.id = relation.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                  and relation.location_kind = 'secure_container'
                order by relation.definition_id;

                select
                    relation.definition_id as "DefinitionId",
                    relation.policy_kind as "Value"
                from item_definition_default_policies relation
                join item_definitions definition on definition.id = relation.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by relation.definition_id, relation.policy_kind;

                select
                    bag.definition_id as "DefinitionId",
                    bag.carry_capacity_bonus as "CarryCapacityBonus"
                from bag_definitions bag
                join item_definitions definition on definition.id = bag.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by bag.definition_id;

                select
                    slot.definition_id as "DefinitionId",
                    slot.slot_index as "SlotIndex",
                    slot.slot_kind as "SlotKind"
                from bag_definition_slots slot
                join item_definitions definition on definition.id = slot.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by slot.definition_id, slot.slot_index;

                select
                    slot_tag.definition_id as "DefinitionId",
                    slot_tag.slot_index as "SlotIndex",
                    slot_tag.tag_id as "TagId"
                from bag_definition_slot_tags slot_tag
                join item_definitions definition on definition.id = slot_tag.definition_id
                where definition.catalog_id = @CatalogId
                  and definition.catalog_revision = @Revision
                  and definition.is_active
                order by slot_tag.definition_id, slot_tag.slot_index, slot_tag.tag_id;

                select id as "Id", display_name as "DisplayName", slot_capacity as "SlotCapacity"
                from secure_container_tiers
                where catalog_id = @CatalogId
                  and catalog_revision = @Revision
                  and is_active
                order by id;
                """,
                new { catalog.CatalogId, catalog.Revision },
                transaction,
                cancellationToken: cancellationToken));

            var categories = (await grid.ReadAsync<IdentityRow>()).ToArray();
            var tags = (await grid.ReadAsync<IdentityRow>()).ToArray();
            var equipmentSlots = (await grid.ReadAsync<EquipmentSlotRow>()).ToArray();
            var definitions = (await grid.ReadAsync<DefinitionRow>()).ToArray();
            var definitionTags = (await grid.ReadAsync<DefinitionValueRow>()).ToArray();
            var definitionEquipmentSlots = (await grid.ReadAsync<DefinitionValueRow>()).ToArray();
            var secureEligibility = (await grid.ReadAsync<DefinitionEligibilityRow>())
                .ToDictionary(row => row.DefinitionId, row => row.IsAllowed, StringComparer.Ordinal);
            var defaultPolicies = (await grid.ReadAsync<DefinitionValueRow>()).ToArray();
            var bags = (await grid.ReadAsync<BagRow>())
                .ToDictionary(row => row.DefinitionId, StringComparer.Ordinal);
            var bagSlots = (await grid.ReadAsync<BagSlotRow>()).ToArray();
            var bagSlotTags = (await grid.ReadAsync<BagSlotTagRow>()).ToArray();
            var secureContainerTiers = (await grid.ReadAsync<SecureContainerTierRow>()).ToArray();

            var tagsByDefinition = definitionTags.ToLookup(
                row => row.DefinitionId,
                row => row.Value,
                StringComparer.Ordinal);
            var equipmentSlotsByDefinition = definitionEquipmentSlots.ToLookup(
                row => row.DefinitionId,
                row => row.Value,
                StringComparer.Ordinal);
            var policiesByDefinition = defaultPolicies.ToLookup(
                row => row.DefinitionId,
                row => row.Value,
                StringComparer.Ordinal);
            var bagSlotsByDefinition = bagSlots.ToLookup(
                row => row.DefinitionId,
                StringComparer.Ordinal);
            var bagSlotTagsBySlot = bagSlotTags.ToLookup(
                row => (row.DefinitionId, row.SlotIndex),
                row => row.TagId);

            var response = new ItemCatalogResponse(
                catalog.CatalogId,
                catalog.Revision,
                catalog.FormatVersion,
                catalog.BaseSecureContainerTierId,
                categories.Select(row => new ItemCatalogIdentityResponse(
                    row.Id,
                    row.DisplayName)).ToArray(),
                tags.Select(row => new ItemCatalogIdentityResponse(
                    row.Id,
                    row.DisplayName)).ToArray(),
                equipmentSlots.Select(row => new ItemCatalogEquipmentSlotResponse(
                    row.Id,
                    row.DisplayName,
                    row.SortOrder)).ToArray(),
                definitions.Select(definition => new ItemCatalogDefinitionResponse(
                    definition.Id,
                    definition.DisplayName,
                    definition.CategoryId,
                    definition.UnitWeight,
                    definition.MaximumStackSize,
                    definition.PlayerDestroyable,
                    tagsByDefinition[definition.Id].ToArray(),
                    equipmentSlotsByDefinition[definition.Id].ToArray(),
                    secureEligibility.GetValueOrDefault(definition.Id),
                    policiesByDefinition[definition.Id].ToArray(),
                    CreateBagResponse(
                        definition.Id,
                        bags,
                        bagSlotsByDefinition,
                        bagSlotTagsBySlot))).ToArray(),
                secureContainerTiers.Select(tier =>
                    new ItemCatalogSecureContainerTierResponse(
                        tier.Id,
                        tier.DisplayName,
                        tier.SlotCapacity)).ToArray());

            await transaction.CommitAsync(cancellationToken);
            return ServiceResult<ItemCatalogResponse>.Ok(response);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ItemCatalogBagResponse? CreateBagResponse(
        string definitionId,
        IReadOnlyDictionary<string, BagRow> bags,
        ILookup<string, BagSlotRow> bagSlotsByDefinition,
        ILookup<(string DefinitionId, int SlotIndex), string> bagSlotTagsBySlot)
    {
        if (!bags.TryGetValue(definitionId, out var bag))
        {
            return null;
        }

        return new ItemCatalogBagResponse(
            bag.CarryCapacityBonus,
            bagSlotsByDefinition[definitionId]
                .Select(slot => new ItemCatalogBagSlotResponse(
                    slot.SlotIndex,
                    slot.SlotKind,
                    bagSlotTagsBySlot[(definitionId, slot.SlotIndex)].ToArray()))
                .ToArray());
    }

    private sealed class CatalogRow
    {
        public string CatalogId { get; set; } = string.Empty;

        public string Revision { get; set; } = string.Empty;

        public int FormatVersion { get; set; }

        public string BaseSecureContainerTierId { get; set; } = string.Empty;
    }

    private class IdentityRow
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class EquipmentSlotRow : IdentityRow
    {
        public int SortOrder { get; set; }
    }

    private sealed class DefinitionRow
    {
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string CategoryId { get; set; } = string.Empty;

        public long UnitWeight { get; set; }

        public int MaximumStackSize { get; set; }

        public bool PlayerDestroyable { get; set; }
    }

    private sealed class DefinitionValueRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    private sealed class DefinitionEligibilityRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public bool IsAllowed { get; set; }
    }

    private sealed class BagRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public long CarryCapacityBonus { get; set; }
    }

    private sealed class BagSlotRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public int SlotIndex { get; set; }

        public string SlotKind { get; set; } = string.Empty;
    }

    private sealed class BagSlotTagRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public int SlotIndex { get; set; }

        public string TagId { get; set; } = string.Empty;
    }

    private sealed class SecureContainerTierRow : IdentityRow
    {
        public int SlotCapacity { get; set; }
    }
}
