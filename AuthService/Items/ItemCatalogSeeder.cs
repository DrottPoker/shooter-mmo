using Dapper;
using Npgsql;
using ShooterMmo.WorldData.Items;

namespace AuthService.Items;

public sealed class ItemCatalogSeeder(
    NpgsqlDataSource dataSource,
    ItemCatalogSource catalogSource,
    ILogger<ItemCatalogSeeder> logger)
{
    private const long CatalogLockId = 7_104_202_607_151_230;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            catalogSource.RuntimeCatalogPath,
            cancellationToken);
        await SeedAsync(catalog, cancellationToken);
    }

    public async Task SeedAsync(
        ItemCatalogRuntimeDocument catalog,
        CancellationToken cancellationToken)
    {
        var canonical = ItemCatalogRuntimeLoader.ValidateAndCanonicalize(catalog);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var revisionChanged = false;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "select pg_advisory_xact_lock(@CatalogLockId);",
                new { CatalogLockId },
                transaction,
                cancellationToken: cancellationToken));

            var currentRevision = await connection.QuerySingleOrDefaultAsync<CatalogRevisionRow>(
                new CommandDefinition(
                    """
                    select
                        catalog_id as "CatalogId",
                        revision as "Revision",
                        base_secure_container_tier_id as "BaseSecureContainerTierId"
                    from item_catalog_revisions
                    where catalog_id = @CatalogId
                      and is_current;
                    """,
                    new { canonical.CatalogId },
                    transaction,
                    cancellationToken: cancellationToken));

            var existingCategoryIds = (await connection.QueryAsync<string>(new CommandDefinition(
                    "select id from item_categories where catalog_id = @CatalogId;",
                    new { canonical.CatalogId },
                    transaction,
                    cancellationToken: cancellationToken)))
                .ToArray();
            var existingTagIds = (await connection.QueryAsync<string>(new CommandDefinition(
                    "select id from item_tags where catalog_id = @CatalogId;",
                    new { canonical.CatalogId },
                    transaction,
                    cancellationToken: cancellationToken)))
                .ToArray();
            var existingEquipmentSlots = (await connection.QueryAsync<EquipmentSlotRow>(
                    new CommandDefinition(
                        """
                        select id as "Id", sort_order as "SortOrder"
                        from equipment_slots;
                        """,
                        transaction: transaction,
                        cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            var existingDefinitions = (await connection.QueryAsync<DefinitionCompatibilityRow>(
                    new CommandDefinition(
                        """
                        select
                            id as "Id",
                            structural_fingerprint as "StructuralFingerprint",
                            is_active as "IsActive"
                        from item_definitions
                        where catalog_id = @CatalogId;
                        """,
                        new { canonical.CatalogId },
                        transaction,
                        cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            var existingTiers = (await connection.QueryAsync<TierCompatibilityRow>(
                    new CommandDefinition(
                        """
                        select
                            id as "Id",
                            structural_fingerprint as "StructuralFingerprint",
                            is_active as "IsActive"
                        from secure_container_tiers
                        where catalog_id = @CatalogId;
                        """,
                        new { canonical.CatalogId },
                        transaction,
                        cancellationToken: cancellationToken)))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);

            EnsureNoIdentityRemoval(
                "category",
                existingCategoryIds,
                canonical.Categories.Select(entry => entry.Id));
            EnsureNoIdentityRemoval(
                "tag",
                existingTagIds,
                canonical.Tags.Select(entry => entry.Id));
            EnsureNoIdentityRemoval(
                "equipment slot",
                existingEquipmentSlots.Keys,
                canonical.EquipmentSlots.Select(entry => entry.Id));
            EnsureNoIdentityRemoval(
                "item definition",
                existingDefinitions.Keys,
                canonical.Definitions.Select(definition => definition.Id));
            EnsureNoIdentityRemoval(
                "Secure Container tier",
                existingTiers.Keys,
                canonical.SecureContainerTiers.Select(tier => tier.Id));

            foreach (var definition in canonical.Definitions)
            {
                if (!existingDefinitions.TryGetValue(definition.Id, out var existing))
                {
                    continue;
                }

                if (!existing.IsActive)
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Retired item definition id '{definition.Id}' cannot be reused.");
                }

                if (string.Equals(
                    existing.StructuralFingerprint,
                    definition.StructuralFingerprint,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var hasLiveInstances = await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        select exists (
                            select 1
                            from item_instances
                            where definition_id = @DefinitionId);
                        """,
                        new { DefinitionId = definition.Id },
                        transaction,
                        cancellationToken: cancellationToken));
                if (hasLiveInstances)
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Item definition '{definition.Id}' has live instances and changed "
                        + "structurally. Apply an explicit data migration before startup.");
                }
            }

            foreach (var tier in canonical.SecureContainerTiers)
            {
                if (!existingTiers.TryGetValue(tier.Id, out var existing))
                {
                    continue;
                }

                if (!existing.IsActive)
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Retired Secure Container tier id '{tier.Id}' cannot be reused.");
                }

                if (string.Equals(
                    existing.StructuralFingerprint,
                    tier.StructuralFingerprint,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var hasEntitlements = await connection.ExecuteScalarAsync<bool>(
                    new CommandDefinition(
                        """
                        select exists (
                            select 1
                            from account_secure_container_entitlements
                            where tier_id = @TierId);
                        """,
                        new { TierId = tier.Id },
                        transaction,
                        cancellationToken: cancellationToken));
                if (hasEntitlements)
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Secure Container tier '{tier.Id}' has live entitlements and changed "
                        + "structurally. Apply an explicit data migration before startup.");
                }
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_catalog_revisions (
                    catalog_id,
                    revision,
                    format_version,
                    base_secure_container_tier_id,
                    is_current)
                values (
                    @CatalogId,
                    @Revision,
                    @FormatVersion,
                    @BaseSecureContainerTierId,
                    false)
                on conflict (catalog_id, revision) do nothing;
                """,
                new
                {
                    canonical.CatalogId,
                    canonical.Revision,
                    canonical.FormatVersion,
                    canonical.BaseSecureContainerTierId
                },
                transaction,
                cancellationToken: cancellationToken));

            var storedRevision = await connection.QuerySingleAsync<CatalogRevisionMetadataRow>(
                new CommandDefinition(
                    """
                    select
                        format_version as "FormatVersion",
                        base_secure_container_tier_id as "BaseSecureContainerTierId"
                    from item_catalog_revisions
                    where catalog_id = @CatalogId
                      and revision = @Revision;
                    """,
                    new { canonical.CatalogId, canonical.Revision },
                    transaction,
                    cancellationToken: cancellationToken));
            if (storedRevision.FormatVersion != canonical.FormatVersion
                || !string.Equals(
                    storedRevision.BaseSecureContainerTierId,
                    canonical.BaseSecureContainerTierId,
                    StringComparison.Ordinal))
            {
                throw new ItemCatalogCompatibilityException(
                    $"Catalog revision '{canonical.Revision}' conflicts with stored metadata.");
            }

            foreach (var category in canonical.Categories)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_categories (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        is_active)
                    values (@Id, @CatalogId, @Revision, @DisplayName, true)
                    on conflict (id) do update
                    set catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        display_name = excluded.display_name,
                        is_active = true,
                        updated_at = now()
                    where item_categories.catalog_id is distinct from excluded.catalog_id
                       or item_categories.catalog_revision is distinct from excluded.catalog_revision
                       or item_categories.display_name is distinct from excluded.display_name
                       or not item_categories.is_active;
                    """,
                    new
                    {
                        category.Id,
                        canonical.CatalogId,
                        canonical.Revision,
                        category.DisplayName
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            foreach (var tag in canonical.Tags)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_tags (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        is_active)
                    values (@Id, @CatalogId, @Revision, @DisplayName, true)
                    on conflict (id) do update
                    set catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        display_name = excluded.display_name,
                        is_active = true,
                        updated_at = now()
                    where item_tags.catalog_id is distinct from excluded.catalog_id
                       or item_tags.catalog_revision is distinct from excluded.catalog_revision
                       or item_tags.display_name is distinct from excluded.display_name
                       or not item_tags.is_active;
                    """,
                    new
                    {
                        tag.Id,
                        canonical.CatalogId,
                        canonical.Revision,
                        tag.DisplayName
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            for (var index = 0; index < canonical.EquipmentSlots.Length; index++)
            {
                var slot = canonical.EquipmentSlots[index];
                var sortOrder = existingEquipmentSlots.TryGetValue(slot.Id, out var existingSlot)
                    ? existingSlot.SortOrder
                    : 1000 + index;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into equipment_slots (
                        id,
                        display_name,
                        sort_order,
                        catalog_id,
                        catalog_revision)
                    values (@Id, @DisplayName, @SortOrder, @CatalogId, @Revision)
                    on conflict (id) do update
                    set display_name = excluded.display_name,
                        catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        updated_at = now()
                    where equipment_slots.display_name is distinct from excluded.display_name
                       or equipment_slots.catalog_id is distinct from excluded.catalog_id
                       or equipment_slots.catalog_revision is distinct from excluded.catalog_revision;
                    """,
                    new
                    {
                        slot.Id,
                        slot.DisplayName,
                        SortOrder = sortOrder,
                        canonical.CatalogId,
                        canonical.Revision
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            foreach (var definition in canonical.Definitions)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into item_definitions (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        category_id,
                        unit_weight,
                        maximum_stack_size,
                        player_destroyable,
                        structural_fingerprint,
                        is_active)
                    values (
                        @Id,
                        @CatalogId,
                        @Revision,
                        @DisplayName,
                        @Category,
                        @UnitWeight,
                        @MaximumStackSize,
                        @PlayerDestroyable,
                        @StructuralFingerprint,
                        true)
                    on conflict (id) do update
                    set catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        display_name = excluded.display_name,
                        category_id = excluded.category_id,
                        unit_weight = excluded.unit_weight,
                        maximum_stack_size = excluded.maximum_stack_size,
                        player_destroyable = excluded.player_destroyable,
                        structural_fingerprint = excluded.structural_fingerprint,
                        is_active = true,
                        updated_at = now()
                    where item_definitions.catalog_id is distinct from excluded.catalog_id
                       or item_definitions.catalog_revision is distinct from excluded.catalog_revision
                       or item_definitions.display_name is distinct from excluded.display_name
                       or item_definitions.category_id is distinct from excluded.category_id
                       or item_definitions.unit_weight is distinct from excluded.unit_weight
                       or item_definitions.maximum_stack_size is distinct from excluded.maximum_stack_size
                       or item_definitions.player_destroyable is distinct from excluded.player_destroyable
                       or item_definitions.structural_fingerprint is distinct from excluded.structural_fingerprint
                       or not item_definitions.is_active;
                    """,
                    new
                    {
                        definition.Id,
                        canonical.CatalogId,
                        canonical.Revision,
                        definition.DisplayName,
                        definition.Category,
                        definition.UnitWeight,
                        definition.MaximumStackSize,
                        definition.PlayerDestroyable,
                        definition.StructuralFingerprint
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                await SynchronizeDefinitionStructureAsync(
                    connection,
                    transaction,
                    definition,
                    cancellationToken);
            }

            foreach (var tier in canonical.SecureContainerTiers)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into secure_container_tiers (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        slot_capacity,
                        structural_fingerprint,
                        is_active)
                    values (
                        @Id,
                        @CatalogId,
                        @Revision,
                        @DisplayName,
                        @SlotCapacity,
                        @StructuralFingerprint,
                        true)
                    on conflict (id) do update
                    set catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        display_name = excluded.display_name,
                        slot_capacity = excluded.slot_capacity,
                        structural_fingerprint = excluded.structural_fingerprint,
                        is_active = true,
                        updated_at = now()
                    where secure_container_tiers.catalog_id is distinct from excluded.catalog_id
                       or secure_container_tiers.catalog_revision is distinct from excluded.catalog_revision
                       or secure_container_tiers.display_name is distinct from excluded.display_name
                       or secure_container_tiers.slot_capacity is distinct from excluded.slot_capacity
                       or secure_container_tiers.structural_fingerprint is distinct from excluded.structural_fingerprint
                       or not secure_container_tiers.is_active;
                    """,
                    new
                    {
                        tier.Id,
                        canonical.CatalogId,
                        canonical.Revision,
                        tier.DisplayName,
                        tier.SlotCapacity,
                        tier.StructuralFingerprint
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                update item_catalog_revisions
                set is_current = false
                where catalog_id = @CatalogId
                  and revision <> @Revision
                  and is_current;

                update item_catalog_revisions
                set is_current = true
                where catalog_id = @CatalogId
                  and revision = @Revision;
                """,
                new { canonical.CatalogId, canonical.Revision },
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            revisionChanged = !string.Equals(
                currentRevision?.Revision,
                canonical.Revision,
                StringComparison.Ordinal);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        if (revisionChanged)
        {
            logger.LogInformation(
                "Mirrored item catalog {CatalogId} revision {CatalogRevision} with {DefinitionCount} definitions.",
                canonical.CatalogId,
                canonical.Revision,
                canonical.Definitions.Length);
        }
    }

    private static async Task SynchronizeDefinitionStructureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemDefinition definition,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from item_definition_tags where definition_id = @DefinitionId;
            delete from item_definition_equipment_slots where definition_id = @DefinitionId;
            delete from item_definition_location_rules where definition_id = @DefinitionId;
            delete from item_definition_default_policies where definition_id = @DefinitionId;
            delete from bag_definitions where definition_id = @DefinitionId;
            """,
            new { DefinitionId = definition.Id },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var tag in definition.Tags)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_definition_tags (definition_id, tag_id)
                values (@DefinitionId, @TagId);
                """,
                new { DefinitionId = definition.Id, TagId = tag },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var equipmentSlot in definition.EquipmentSlots)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_definition_equipment_slots (
                    definition_id,
                    equipment_slot_id)
                values (@DefinitionId, @EquipmentSlotId);
                """,
                new
                {
                    DefinitionId = definition.Id,
                    EquipmentSlotId = equipmentSlot
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into item_definition_location_rules (
                definition_id,
                location_kind,
                is_allowed)
            values (@DefinitionId, 'secure_container', @IsAllowed);
            """,
            new
            {
                DefinitionId = definition.Id,
                IsAllowed = definition.LocationEligibility.SecureContainer
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var policy in definition.DefaultPolicies)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into item_definition_default_policies (definition_id, policy_kind)
                values (@DefinitionId, @PolicyKind);
                """,
                new { DefinitionId = definition.Id, PolicyKind = policy },
                transaction,
                cancellationToken: cancellationToken));
        }

        if (definition.Bag is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into bag_definitions (definition_id, carry_capacity_bonus)
            values (@DefinitionId, @CarryCapacityBonus);
            """,
            new
            {
                DefinitionId = definition.Id,
                definition.Bag.CarryCapacityBonus
            },
            transaction,
            cancellationToken: cancellationToken));

        foreach (var slot in definition.Bag.Slots)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into bag_definition_slots (definition_id, slot_index, slot_kind)
                values (@DefinitionId, @SlotIndex, @SlotKind);
                """,
                new
                {
                    DefinitionId = definition.Id,
                    SlotIndex = slot.Index,
                    SlotKind = slot.Kind
                },
                transaction,
                cancellationToken: cancellationToken));

            foreach (var acceptedTag in slot.AcceptedTags)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into bag_definition_slot_tags (
                        definition_id,
                        slot_index,
                        tag_id)
                    values (@DefinitionId, @SlotIndex, @TagId);
                    """,
                    new
                    {
                        DefinitionId = definition.Id,
                        SlotIndex = slot.Index,
                        TagId = acceptedTag
                    },
                    transaction,
                    cancellationToken: cancellationToken));
            }
        }
    }

    private static void EnsureNoIdentityRemoval(
        string identityKind,
        IEnumerable<string> existingIds,
        IEnumerable<string> candidateIds)
    {
        var candidateSet = candidateIds.ToHashSet(StringComparer.Ordinal);
        var removedId = existingIds.FirstOrDefault(id => !candidateSet.Contains(id));
        if (removedId is not null)
        {
            throw new ItemCatalogCompatibilityException(
                $"Existing {identityKind} id '{removedId}' is missing from the candidate catalog. "
                + "Retirement requires an explicit migration.");
        }
    }

    private sealed record CatalogRevisionRow(
        string CatalogId,
        string Revision,
        string BaseSecureContainerTierId);

    private sealed record CatalogRevisionMetadataRow(
        int FormatVersion,
        string BaseSecureContainerTierId);

    private sealed record EquipmentSlotRow(string Id, int SortOrder);

    private sealed record DefinitionCompatibilityRow(
        string Id,
        string StructuralFingerprint,
        bool IsActive);

    private sealed record TierCompatibilityRow(
        string Id,
        string StructuralFingerprint,
        bool IsActive);
}
