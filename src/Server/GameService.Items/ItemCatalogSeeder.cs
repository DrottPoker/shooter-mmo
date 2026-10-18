using Dapper;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items;

// Mirrors the compiled item catalog into PostgreSQL before item traffic is accepted. Ids
// are never removed: an id that leaves the catalog must be listed as retired, and its row
// is retired once nothing live references it. A change that live state depends on stops
// startup until an explicit data migration makes the state compatible.
internal sealed class ItemCatalogSeeder(
    NpgsqlDataSource dataSource,
    ItemCatalogSource catalogSource,
    ILogger<ItemCatalogSeeder> logger)
{
    private const long CatalogLockId = 7_104_202_607_151_230;
    private const string CharacterSettingsId = "character_default";
    private const string MigrationAdvice =
        "Apply an explicit data migration, or reset a local development database.";

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

            var mirror = new CatalogMirror(connection, transaction, canonical, cancellationToken);
            var existing = await mirror.LoadExistingAsync();
            existing.EnsureCompatibleIdentities(canonical);
            await mirror.EnsureCompatibleStructureAsync(existing);
            await mirror.EnsureCompatibleCharacterSettingsAsync();

            await mirror.WriteRevisionAsync();
            await mirror.WriteVocabularyAsync(existing);
            await mirror.WriteDefinitionsAsync();
            await mirror.WriteSecureContainerTiersAsync();
            await mirror.WriteVendorsAsync();
            await mirror.RetireAsync(existing);
            await mirror.WriteCharacterSettingsAsync();
            await mirror.WriteEconomySettingsAsync();
            await mirror.MakeRevisionCurrentAsync();

            await transaction.CommitAsync(cancellationToken);
            revisionChanged = !string.Equals(
                existing.CurrentRevision,
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

    // The ids and states that the database holds before this catalog is mirrored.
    private sealed class ExistingCatalog
    {
        public string? CurrentRevision { get; init; }

        public required IReadOnlyDictionary<string, bool> Categories { get; init; }

        public required IReadOnlyDictionary<string, bool> Tags { get; init; }

        public required IReadOnlyDictionary<string, EquipmentSlotRow> EquipmentSlots { get; init; }

        public required IReadOnlyDictionary<string, CompatibilityRow> Definitions { get; init; }

        public required IReadOnlyDictionary<string, CompatibilityRow> SecureContainerTiers { get; init; }

        public void EnsureCompatibleIdentities(ItemCatalogRuntimeDocument canonical)
        {
            EnsureCompatibleIdentity(
                "category",
                Categories,
                canonical.Categories.Select(entry => entry.Id),
                canonical.Retired.Categories);
            EnsureCompatibleIdentity(
                "tag",
                Tags,
                canonical.Tags.Select(entry => entry.Id),
                canonical.Retired.Tags);
            EnsureCompatibleIdentity(
                "equipment slot",
                EquipmentSlots.ToDictionary(pair => pair.Key, pair => pair.Value.IsActive),
                canonical.EquipmentSlots.Select(slot => slot.Id),
                canonical.Retired.EquipmentSlots);
            EnsureCompatibleIdentity(
                "item definition",
                Definitions.ToDictionary(pair => pair.Key, pair => pair.Value.IsActive),
                canonical.Definitions.Select(definition => definition.Id),
                canonical.Retired.Definitions);
            EnsureCompatibleIdentity(
                "Secure Container tier",
                SecureContainerTiers.ToDictionary(pair => pair.Key, pair => pair.Value.IsActive),
                canonical.SecureContainerTiers.Select(tier => tier.Id),
                canonical.Retired.SecureContainerTiers);
        }

        private static void EnsureCompatibleIdentity(
            string identityKind,
            IReadOnlyDictionary<string, bool> existing,
            IEnumerable<string> activeIds,
            IEnumerable<string> retiredIds)
        {
            var active = activeIds.ToHashSet(StringComparer.Ordinal);
            var retired = retiredIds.ToHashSet(StringComparer.Ordinal);
            foreach (var (id, isActive) in existing.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (active.Contains(id))
                {
                    if (!isActive)
                    {
                        throw new ItemCatalogCompatibilityException(
                            $"Retired {identityKind} id '{id}' cannot be reused.");
                    }

                    continue;
                }

                if (!retired.Contains(id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Existing {identityKind} id '{id}' is missing from the candidate catalog. "
                        + "List it as retired instead of removing it.");
                }
            }
        }
    }

    private sealed class CatalogMirror(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemCatalogRuntimeDocument canonical,
        CancellationToken cancellationToken)
    {
        public async Task<ExistingCatalog> LoadExistingAsync()
        {
            var currentRevision = await connection.ExecuteScalarAsync<string?>(Command(
                """
                select revision
                from item_catalog_revisions
                where catalog_id = @CatalogId
                  and is_current;
                """,
                new { canonical.CatalogId }));
            var categories = await QueryActivityAsync(
                "select id as \"Id\", is_active as \"IsActive\" from item_categories where catalog_id = @CatalogId;");
            var tags = await QueryActivityAsync(
                "select id as \"Id\", is_active as \"IsActive\" from item_tags where catalog_id = @CatalogId;");
            var equipmentSlots = (await connection.QueryAsync<EquipmentSlotRow>(Command(
                    """
                    select
                        id as "Id",
                        maximum_quantity as "MaximumQuantity",
                        is_active as "IsActive"
                    from equipment_slots;
                    """)))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            var definitions = (await connection.QueryAsync<CompatibilityRow>(Command(
                    """
                    select
                        id as "Id",
                        structural_fingerprint as "StructuralFingerprint",
                        is_active as "IsActive"
                    from item_definitions
                    where catalog_id = @CatalogId;
                    """,
                    new { canonical.CatalogId })))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            var tiers = (await connection.QueryAsync<CompatibilityRow>(Command(
                    """
                    select
                        id as "Id",
                        structural_fingerprint as "StructuralFingerprint",
                        is_active as "IsActive"
                    from secure_container_tiers
                    where catalog_id = @CatalogId;
                    """,
                    new { canonical.CatalogId })))
                .ToDictionary(row => row.Id, StringComparer.Ordinal);
            return new ExistingCatalog
            {
                CurrentRevision = currentRevision,
                Categories = categories,
                Tags = tags,
                EquipmentSlots = equipmentSlots,
                Definitions = definitions,
                SecureContainerTiers = tiers
            };
        }

        // A structural change is accepted only while no live state depends on it.
        public async Task EnsureCompatibleStructureAsync(ExistingCatalog existing)
        {
            foreach (var definition in canonical.Definitions)
            {
                if (existing.Definitions.TryGetValue(definition.Id, out var row)
                    && !string.Equals(
                        row.StructuralFingerprint,
                        definition.StructuralFingerprint,
                        StringComparison.Ordinal)
                    && await ExistsAsync(
                        "select exists (select 1 from item_instances where definition_id = @Id);",
                        definition.Id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Item definition '{definition.Id}' has live instances and changed "
                        + "structurally. " + MigrationAdvice);
                }
            }

            foreach (var tier in canonical.SecureContainerTiers)
            {
                if (existing.SecureContainerTiers.TryGetValue(tier.Id, out var row)
                    && !string.Equals(
                        row.StructuralFingerprint,
                        tier.StructuralFingerprint,
                        StringComparison.Ordinal)
                    && await ExistsAsync(
                        """
                        select exists (
                            select 1
                            from account_secure_container_entitlements
                            where tier_id = @Id);
                        """,
                        tier.Id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Secure Container tier '{tier.Id}' has live entitlements and changed "
                        + "structurally. " + MigrationAdvice);
                }
            }

            foreach (var slot in canonical.EquipmentSlots)
            {
                if (existing.EquipmentSlots.TryGetValue(slot.Id, out var row)
                    && slot.MaximumQuantity < row.MaximumQuantity
                    && await connection.ExecuteScalarAsync<bool>(Command(
                        """
                        select exists (
                            select 1
                            from item_instances
                            where equipment_slot_id = @Id
                              and quantity > @MaximumQuantity);
                        """,
                        new { slot.Id, slot.MaximumQuantity })))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Equipment slot '{slot.Id}' holds stacks above its new maximum quantity. "
                        + MigrationAdvice);
                }
            }
        }

        // Every character copies the settings when its item state is created, so a change
        // must not leave existing characters on different settings.
        public async Task EnsureCompatibleCharacterSettingsAsync()
        {
            var current = await connection.QuerySingleAsync<CharacterSettingsRow>(Command(
                """
                select
                    base_carry_capacity as "BaseCarryCapacity",
                    permanent_inventory_slot_count as "InventorySlotCount",
                    bank_slot_count as "BankSlotCount"
                from item_system_settings
                where id = @CharacterSettingsId;
                """,
                new { CharacterSettingsId }));
            var candidate = canonical.Character;
            if (current.BaseCarryCapacity == candidate.BaseCarryCapacity
                && current.InventorySlotCount == candidate.InventorySlotCount
                && current.BankSlotCount == candidate.BankSlotCount)
            {
                return;
            }

            if (await connection.ExecuteScalarAsync<bool>(Command(
                    "select exists (select 1 from character_item_states);")))
            {
                throw new ItemCatalogCompatibilityException(
                    "The item catalog character settings changed while characters already have "
                    + "item state. " + MigrationAdvice);
            }
        }

        public async Task WriteRevisionAsync()
        {
            await connection.ExecuteAsync(Command(
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
                }));

            var stored = await connection.QuerySingleAsync<CatalogRevisionMetadataRow>(Command(
                """
                select
                    format_version as "FormatVersion",
                    base_secure_container_tier_id as "BaseSecureContainerTierId"
                from item_catalog_revisions
                where catalog_id = @CatalogId
                  and revision = @Revision;
                """,
                new { canonical.CatalogId, canonical.Revision }));
            if (stored.FormatVersion != canonical.FormatVersion
                || !string.Equals(
                    stored.BaseSecureContainerTierId,
                    canonical.BaseSecureContainerTierId,
                    StringComparison.Ordinal))
            {
                throw new ItemCatalogCompatibilityException(
                    $"Catalog revision '{canonical.Revision}' conflicts with stored metadata.");
            }
        }

        public async Task WriteVocabularyAsync(ExistingCatalog existing)
        {
            foreach (var category in canonical.Categories)
            {
                await connection.ExecuteAsync(Command(
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
                    new { category.Id, canonical.CatalogId, canonical.Revision, category.DisplayName }));
            }

            foreach (var tag in canonical.Tags)
            {
                await connection.ExecuteAsync(Command(
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
                    new { tag.Id, canonical.CatalogId, canonical.Revision, tag.DisplayName }));
            }

            foreach (var toolKind in canonical.ToolKinds)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_tool_kinds (
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
                    where item_tool_kinds.catalog_id is distinct from excluded.catalog_id
                       or item_tool_kinds.catalog_revision is distinct from excluded.catalog_revision
                       or item_tool_kinds.display_name is distinct from excluded.display_name
                       or not item_tool_kinds.is_active;
                    """,
                    new { toolKind.Id, canonical.CatalogId, canonical.Revision, toolKind.DisplayName }));
            }

            // Tool kinds carry no live state, so a kind that left the catalog only turns inactive.
            await connection.ExecuteAsync(Command(
                """
                update item_tool_kinds
                set is_active = false,
                    updated_at = now()
                where is_active
                  and id <> all(@ToolKindIds);
                """,
                new { ToolKindIds = canonical.ToolKinds.Select(kind => kind.Id).ToArray() }));

            // The catalog lists slots in display order.
            for (var index = 0; index < canonical.EquipmentSlots.Length; index++)
            {
                var slot = canonical.EquipmentSlots[index];
                await connection.ExecuteAsync(Command(
                    """
                    insert into equipment_slots (
                        id,
                        display_name,
                        display_order,
                        maximum_quantity,
                        is_active,
                        catalog_id,
                        catalog_revision)
                    values (
                        @Id,
                        @DisplayName,
                        @DisplayOrder,
                        @MaximumQuantity,
                        true,
                        @CatalogId,
                        @Revision)
                    on conflict (id) do update
                    set display_name = excluded.display_name,
                        display_order = excluded.display_order,
                        maximum_quantity = excluded.maximum_quantity,
                        is_active = true,
                        catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        updated_at = now()
                    where equipment_slots.display_name is distinct from excluded.display_name
                       or equipment_slots.display_order is distinct from excluded.display_order
                       or equipment_slots.maximum_quantity is distinct from excluded.maximum_quantity
                       or not equipment_slots.is_active
                       or equipment_slots.catalog_id is distinct from excluded.catalog_id
                       or equipment_slots.catalog_revision is distinct from excluded.catalog_revision;
                    """,
                    new
                    {
                        slot.Id,
                        slot.DisplayName,
                        DisplayOrder = index,
                        slot.MaximumQuantity,
                        canonical.CatalogId,
                        canonical.Revision
                    }));
            }
        }

        public async Task WriteDefinitionsAsync()
        {
            foreach (var definition in canonical.Definitions)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definitions (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        category_id,
                        unit_weight,
                        value,
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
                        @Value,
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
                        value = excluded.value,
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
                       or item_definitions.value is distinct from excluded.value
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
                        definition.Value,
                        definition.MaximumStackSize,
                        definition.PlayerDestroyable,
                        definition.StructuralFingerprint
                    }));

                await WriteDefinitionStructureAsync(definition);
            }
        }

        public async Task WriteSecureContainerTiersAsync()
        {
            foreach (var tier in canonical.SecureContainerTiers)
            {
                await connection.ExecuteAsync(Command(
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
                    }));
            }
        }

        // Vendor offers and sale categories are rewritten with each vendor. A vendor that left
        // the catalog turns inactive, so no purchase or sale can use it again.
        public async Task WriteVendorsAsync()
        {
            foreach (var vendor in canonical.Vendors)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into vendor_profiles (
                        id,
                        catalog_id,
                        catalog_revision,
                        display_name,
                        purchase_price_basis_points,
                        sale_price_basis_points,
                        is_active)
                    values (
                        @Id,
                        @CatalogId,
                        @Revision,
                        @DisplayName,
                        @PurchasePriceBasisPoints,
                        @SalePriceBasisPoints,
                        true)
                    on conflict (id) do update
                    set catalog_id = excluded.catalog_id,
                        catalog_revision = excluded.catalog_revision,
                        display_name = excluded.display_name,
                        purchase_price_basis_points = excluded.purchase_price_basis_points,
                        sale_price_basis_points = excluded.sale_price_basis_points,
                        is_active = true,
                        updated_at = now()
                    where vendor_profiles.catalog_id is distinct from excluded.catalog_id
                       or vendor_profiles.catalog_revision is distinct from excluded.catalog_revision
                       or vendor_profiles.display_name is distinct from excluded.display_name
                       or vendor_profiles.purchase_price_basis_points
                           is distinct from excluded.purchase_price_basis_points
                       or vendor_profiles.sale_price_basis_points
                           is distinct from excluded.sale_price_basis_points
                       or not vendor_profiles.is_active;

                    delete from vendor_offers where vendor_id = @Id;
                    delete from vendor_sale_categories where vendor_id = @Id;
                    """,
                    new
                    {
                        vendor.Id,
                        canonical.CatalogId,
                        canonical.Revision,
                        vendor.DisplayName,
                        vendor.PurchasePriceBasisPoints,
                        vendor.SalePriceBasisPoints
                    }));

                for (var index = 0; index < vendor.Offers.Length; index++)
                {
                    await connection.ExecuteAsync(Command(
                        """
                        insert into vendor_offers (vendor_id, offer_index, definition_id)
                        values (@VendorId, @OfferIndex, @DefinitionId);
                        """,
                        new
                        {
                            VendorId = vendor.Id,
                            OfferIndex = index,
                            vendor.Offers[index].DefinitionId
                        }));
                }

                foreach (var category in vendor.SaleCategories)
                {
                    await connection.ExecuteAsync(Command(
                        """
                        insert into vendor_sale_categories (vendor_id, category_id)
                        values (@VendorId, @CategoryId);
                        """,
                        new { VendorId = vendor.Id, CategoryId = category }));
                }
            }

            await connection.ExecuteAsync(Command(
                """
                update vendor_profiles
                set is_active = false,
                    updated_at = now()
                where is_active
                  and id <> all(@VendorIds);
                """,
                new { VendorIds = canonical.Vendors.Select(vendor => vendor.Id).ToArray() }));
        }

        // Definitions first: active definitions only reference active vocabulary, so once
        // the retired definitions are inactive nothing active references a retired id.
        public async Task RetireAsync(ExistingCatalog existing)
        {
            foreach (var id in ActiveExisting(
                canonical.Retired.Definitions,
                id => existing.Definitions.TryGetValue(id, out var row) && row.IsActive))
            {
                if (await ExistsAsync(
                        "select exists (select 1 from item_instances where definition_id = @Id);",
                        id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Retired item definition '{id}' still has live item instances. "
                        + MigrationAdvice);
                }

                await RetireRowAsync("item_definitions", id);
            }

            foreach (var id in ActiveExisting(
                canonical.Retired.EquipmentSlots,
                id => existing.EquipmentSlots.TryGetValue(id, out var row) && row.IsActive))
            {
                if (await ExistsAsync(
                        "select exists (select 1 from item_instances where equipment_slot_id = @Id);",
                        id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Retired equipment slot '{id}' still holds equipped items. "
                        + MigrationAdvice);
                }

                await RetireRowAsync("equipment_slots", id);
            }

            foreach (var id in ActiveExisting(
                canonical.Retired.SecureContainerTiers,
                id => existing.SecureContainerTiers.TryGetValue(id, out var row) && row.IsActive))
            {
                if (await ExistsAsync(
                        """
                        select exists (
                            select 1
                            from account_secure_container_entitlements
                            where tier_id = @Id);
                        """,
                        id))
                {
                    throw new ItemCatalogCompatibilityException(
                        $"Retired Secure Container tier '{id}' still has account entitlements. "
                        + MigrationAdvice);
                }

                await RetireRowAsync("secure_container_tiers", id);
            }

            foreach (var id in ActiveExisting(
                canonical.Retired.Categories,
                id => existing.Categories.TryGetValue(id, out var active) && active))
            {
                await RetireRowAsync("item_categories", id);
            }

            foreach (var id in ActiveExisting(
                canonical.Retired.Tags,
                id => existing.Tags.TryGetValue(id, out var active) && active))
            {
                await RetireRowAsync("item_tags", id);
            }
        }

        public async Task WriteCharacterSettingsAsync()
        {
            await connection.ExecuteAsync(Command(
                """
                update item_system_settings
                set catalog_id = @CatalogId,
                    base_carry_capacity = @BaseCarryCapacity,
                    permanent_inventory_slot_count = @InventorySlotCount,
                    bank_slot_count = @BankSlotCount
                where id = @CharacterSettingsId
                  and (catalog_id is distinct from @CatalogId
                       or base_carry_capacity is distinct from @BaseCarryCapacity
                       or permanent_inventory_slot_count is distinct from @InventorySlotCount
                       or bank_slot_count is distinct from @BankSlotCount);
                """,
                new
                {
                    CharacterSettingsId,
                    canonical.CatalogId,
                    canonical.Character.BaseCarryCapacity,
                    canonical.Character.InventorySlotCount,
                    canonical.Character.BankSlotCount
                }));
        }

        // The currency settings only affect characters created later, so they may change at
        // any time, unlike the character item settings.
        public async Task WriteEconomySettingsAsync()
        {
            await connection.ExecuteAsync(Command(
                """
                update item_system_settings
                set currency_display_name = @CurrencyDisplayName,
                    starting_currency_balance = @StartingBalance
                where id = @CharacterSettingsId
                  and (currency_display_name is distinct from @CurrencyDisplayName
                       or starting_currency_balance is distinct from @StartingBalance);
                """,
                new
                {
                    CharacterSettingsId,
                    canonical.Economy.CurrencyDisplayName,
                    canonical.Economy.StartingBalance
                }));
        }

        public async Task MakeRevisionCurrentAsync()
        {
            await connection.ExecuteAsync(Command(
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
                new { canonical.CatalogId, canonical.Revision }));
        }

        private async Task WriteDefinitionStructureAsync(ItemDefinition definition)
        {
            await connection.ExecuteAsync(Command(
                """
                delete from item_definition_tags where definition_id = @DefinitionId;
                delete from item_definition_tool_kinds where definition_id = @DefinitionId;
                delete from item_definition_equipment_slots where definition_id = @DefinitionId;
                delete from item_definition_blocked_equipment_slots where definition_id = @DefinitionId;
                delete from item_definition_location_rules where definition_id = @DefinitionId;
                delete from item_definition_default_policies where definition_id = @DefinitionId;
                delete from bag_definitions where definition_id = @DefinitionId;
                """,
                new { DefinitionId = definition.Id }));

            foreach (var tag in definition.Tags)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definition_tags (definition_id, tag_id)
                    values (@DefinitionId, @TagId);
                    """,
                    new { DefinitionId = definition.Id, TagId = tag }));
            }

            foreach (var toolKind in definition.ToolKinds)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definition_tool_kinds (definition_id, tool_kind_id)
                    values (@DefinitionId, @ToolKindId);
                    """,
                    new { DefinitionId = definition.Id, ToolKindId = toolKind }));
            }

            foreach (var equipmentSlot in definition.EquipmentSlots)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definition_equipment_slots (definition_id, equipment_slot_id)
                    values (@DefinitionId, @EquipmentSlotId);
                    """,
                    new { DefinitionId = definition.Id, EquipmentSlotId = equipmentSlot }));
            }

            foreach (var blockedSlot in definition.BlockedEquipmentSlots)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definition_blocked_equipment_slots (
                        definition_id,
                        equipment_slot_id)
                    values (@DefinitionId, @EquipmentSlotId);
                    """,
                    new { DefinitionId = definition.Id, EquipmentSlotId = blockedSlot }));
            }

            await connection.ExecuteAsync(Command(
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
                }));

            foreach (var policy in definition.DefaultPolicies)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into item_definition_default_policies (definition_id, policy_kind)
                    values (@DefinitionId, @PolicyKind);
                    """,
                    new { DefinitionId = definition.Id, PolicyKind = policy }));
            }

            if (definition.Bag is null)
            {
                return;
            }

            await connection.ExecuteAsync(Command(
                """
                insert into bag_definitions (definition_id, carry_capacity_bonus)
                values (@DefinitionId, @CarryCapacityBonus);
                """,
                new { DefinitionId = definition.Id, definition.Bag.CarryCapacityBonus }));

            foreach (var slot in definition.Bag.Slots)
            {
                await connection.ExecuteAsync(Command(
                    """
                    insert into bag_definition_slots (definition_id, slot_index, slot_kind)
                    values (@DefinitionId, @SlotIndex, @SlotKind);
                    """,
                    new { DefinitionId = definition.Id, SlotIndex = slot.Index, SlotKind = slot.Kind }));

                foreach (var acceptedTag in slot.AcceptedTags)
                {
                    await connection.ExecuteAsync(Command(
                        """
                        insert into bag_definition_slot_tags (definition_id, slot_index, tag_id)
                        values (@DefinitionId, @SlotIndex, @TagId);
                        """,
                        new { DefinitionId = definition.Id, SlotIndex = slot.Index, TagId = acceptedTag }));
                }
            }
        }

        private async Task RetireRowAsync(string table, string id)
        {
            // The table name is one of the fixed catalog tables above, never input.
            await connection.ExecuteAsync(Command(
                $"""
                update {table}
                set is_active = false,
                    updated_at = now()
                where id = @Id
                  and is_active;
                """,
                new { Id = id }));
        }

        private async Task<IReadOnlyDictionary<string, bool>> QueryActivityAsync(string sql)
        {
            return (await connection.QueryAsync<ActivityRow>(Command(sql, new { canonical.CatalogId })))
                .ToDictionary(row => row.Id, row => row.IsActive, StringComparer.Ordinal);
        }

        private Task<bool> ExistsAsync(string sql, string id)
        {
            return connection.ExecuteScalarAsync<bool>(Command(sql, new { Id = id }));
        }

        private CommandDefinition Command(string sql, object? parameters = null)
        {
            return new CommandDefinition(
                sql,
                parameters,
                transaction,
                cancellationToken: cancellationToken);
        }

        private static IEnumerable<string> ActiveExisting(
            IEnumerable<string> retiredIds,
            Func<string, bool> isActive)
        {
            return retiredIds.Where(isActive);
        }
    }

    private sealed record CatalogRevisionMetadataRow(
        int FormatVersion,
        string BaseSecureContainerTierId);

    private sealed record ActivityRow(string Id, bool IsActive);

    private sealed record EquipmentSlotRow(string Id, int MaximumQuantity, bool IsActive);

    private sealed record CompatibilityRow(
        string Id,
        string StructuralFingerprint,
        bool IsActive);

    private sealed record CharacterSettingsRow(
        long BaseCarryCapacity,
        int InventorySlotCount,
        int BankSlotCount);
}
