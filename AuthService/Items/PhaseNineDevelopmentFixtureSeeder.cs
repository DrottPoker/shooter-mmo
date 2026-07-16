using Dapper;
using Npgsql;

namespace AuthService.Items;

public sealed record PhaseNineDevelopmentFixtureResult(
    Guid CharacterId,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    int GrantedItemCount,
    int RecoveryDeliveryCount);

public static class PhaseNineDevelopmentFixtureCommand
{
    public const string ArgumentName = "--seed-phase9-items";

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out Guid characterId,
        out string error)
    {
        characterId = Guid.Empty;
        error = string.Empty;
        if (arguments is null)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], ArgumentName, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count
                || !Guid.TryParse(arguments[index + 1], out characterId)
                || characterId == Guid.Empty)
            {
                error = $"{ArgumentName} requires a non-empty character GUID.";
                characterId = Guid.Empty;
                return true;
            }

            if (arguments.Skip(index + 2).Any(argument => string.Equals(
                    argument,
                    ArgumentName,
                    StringComparison.Ordinal)))
            {
                error = $"{ArgumentName} may be supplied only once.";
                characterId = Guid.Empty;
            }

            return true;
        }

        return false;
    }
}

public sealed class PhaseNineDevelopmentFixtureSeeder(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    ItemQueryService queryService,
    IHostEnvironment environment)
{
    private static readonly IReadOnlyDictionary<string, RequiredDefinition> RequiredDefinitions =
        new Dictionary<string, RequiredDefinition>(StringComparer.Ordinal)
        {
            ["ammunition.training_556"] = new(60, 1, null),
            ["armor.starter_vest"] = new(1, 20, null),
            ["bag.field_pack"] = new(1, 10, 50),
            ["material.iron_ore"] = new(100, 6, null),
            ["medical.field_dressing"] = new(10, 2, null),
            ["quest_item.signal_transponder"] = new(1, 1, null),
            ["ring.starter_band"] = new(1, 1, null),
            ["tool.starter_pickaxe"] = new(1, 15, null),
            ["weapon.training_rifle"] = new(1, 25, null)
        };

    public async Task<PhaseNineDevelopmentFixtureResult> SeedAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        EnsureGuardrails(characterId);
        var target = await LoadTargetAsync(characterId, cancellationToken);
        await ValidateEmptyOfflineTargetAsync(target, cancellationToken);
        await ValidateCatalogAsync(cancellationToken);

        var grantedItems = new List<ItemTransactionItemRevision>();
        var equippedBag = await GrantAsync(
            target,
            "bag.field_pack",
            1,
            target.PermanentInventoryContainerId,
            0,
            cancellationToken);
        grantedItems.Add(equippedBag);
        await RequireSuccessAsync(
            transactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new EquipItemCommand(
                        target.CharacterId,
                        null,
                        equippedBag.ItemInstanceId,
                        equippedBag.Revision,
                        "bag")),
                cancellationToken),
            "equip the fixture Bag");

        var bagContainerId = await LoadBagContainerIdAsync(
            equippedBag.ItemInstanceId,
            cancellationToken);
        grantedItems.Add(await GrantAsync(
            target,
            "medical.field_dressing",
            5,
            bagContainerId,
            4,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "material.iron_ore",
            5,
            bagContainerId,
            5,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "ammunition.training_556",
            10,
            bagContainerId,
            6,
            cancellationToken));

        grantedItems.Add(await GrantAsync(
            target,
            "bag.field_pack",
            1,
            target.PermanentInventoryContainerId,
            1,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "weapon.training_rifle",
            1,
            target.PermanentInventoryContainerId,
            2,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "armor.starter_vest",
            1,
            target.PermanentInventoryContainerId,
            3,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "tool.starter_pickaxe",
            1,
            target.PermanentInventoryContainerId,
            4,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "ring.starter_band",
            1,
            target.PermanentInventoryContainerId,
            5,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "quest_item.signal_transponder",
            1,
            target.SecureContainerId,
            0,
            cancellationToken));

        grantedItems.Add(await GrantAsync(
            target,
            "material.iron_ore",
            20,
            target.BankContainerId,
            0,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "material.iron_ore",
            16,
            target.BankContainerId,
            1,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "medical.field_dressing",
            1,
            target.BankContainerId,
            2,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "ammunition.training_556",
            1,
            target.BankContainerId,
            3,
            cancellationToken));
        grantedItems.Add(await GrantAsync(
            target,
            "bag.field_pack",
            1,
            target.BankContainerId,
            4,
            cancellationToken));

        var recoveryMaterial = await GrantAsync(
            target,
            "material.iron_ore",
            2,
            target.BankContainerId,
            10,
            cancellationToken);
        var recoveryMedical = await GrantAsync(
            target,
            "medical.field_dressing",
            2,
            target.BankContainerId,
            11,
            cancellationToken);
        grantedItems.Add(recoveryMaterial);
        grantedItems.Add(recoveryMedical);
        await RequireSuccessAsync(
            transactionService.ExecuteAsync(
                new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new AddRecoveryDeliveryCommand(
                        target.CharacterId,
                        null,
                        "development_phase9_fixture",
                        $"phase9:{target.CharacterId:N}",
                        DateTime.UtcNow,
                        null,
                        [
                            new ItemRevisionExpectation(
                                recoveryMaterial.ItemInstanceId,
                                recoveryMaterial.Revision),
                            new ItemRevisionExpectation(
                                recoveryMedical.ItemInstanceId,
                                recoveryMedical.Revision)
                        ])),
                cancellationToken),
            "create the fixture Recovery delivery");

        var snapshotResult = await queryService.GetCharacterInventoryAsync(
            target.AccountId,
            target.CharacterId,
            cancellationToken);
        if (!snapshotResult.Succeeded || snapshotResult.Value is null)
        {
            throw new InvalidOperationException(
                snapshotResult.Error?.Message
                    ?? "The seeded character inventory could not be read back.");
        }

        var snapshot = snapshotResult.Value;
        return new PhaseNineDevelopmentFixtureResult(
            target.CharacterId,
            snapshot.ItemStateRevision,
            snapshot.CarriedWeight,
            snapshot.CarryCapacity,
            grantedItems.Count,
            snapshot.RecoveryStorage.Deliveries.Count);
    }

    private async Task<ItemTransactionItemRevision> GrantAsync(
        FixtureTarget target,
        string definitionId,
        int quantity,
        Guid destinationContainerId,
        int slotIndex,
        CancellationToken cancellationToken)
    {
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    target.CharacterId,
                    null,
                    definitionId,
                    quantity,
                    destinationContainerId,
                    slotIndex)),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not grant fixture definition '{definitionId}': "
                + $"{result.Error?.Code}: {result.Error?.Message}");
        }

        return result.ItemRevisions.Single();
    }

    private async Task<FixtureTarget> LoadTargetAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FixtureTarget>(new CommandDefinition(
            """
            select
                character.id as "CharacterId",
                character.account_id as "AccountId",
                state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                state.bank_container_id as "BankContainerId",
                state.secure_container_id as "SecureContainerId"
            from characters character
            join character_item_states state on state.character_id = character.id
            where character.id = @CharacterId
              and character.deleted_at is null;
            """,
            new { CharacterId = characterId },
            cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException(
                $"Character '{characterId}' does not have an initialized item state.");
    }

    private async Task ValidateEmptyOfflineTargetAsync(
        FixtureTarget target,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            """
            select count(*)
            from character_simulation_sessions
            where character_id = @CharacterId
              and released_at is null
              and expires_at > now();

            select count(*)
            from item_instances item
            left join item_containers container on container.id = item.container_id
            left join item_instances source_bag
              on source_bag.id = container.bound_bag_item_instance_id
            where coalesce(
                    item.equipped_character_id,
                    container.owner_character_id,
                    source_bag.equipped_character_id) = @CharacterId;

            select count(*)
            from recovery_deliveries
            where character_id = @CharacterId;
            """,
            new { target.CharacterId },
            cancellationToken: cancellationToken));
        var activeSessions = await grid.ReadSingleAsync<int>();
        var itemCount = await grid.ReadSingleAsync<int>();
        var recoveryCount = await grid.ReadSingleAsync<int>();
        if (activeSessions != 0)
        {
            throw new InvalidOperationException(
                "The Phase 9 fixture character must be offline while it is seeded.");
        }

        if (itemCount != 0 || recoveryCount != 0)
        {
            throw new InvalidOperationException(
                "The Phase 9 fixture requires a new character with no items or Recovery deliveries.");
        }
    }

    private async Task ValidateCatalogAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<DefinitionRow>(new CommandDefinition(
            """
            select
                definition.id as "Id",
                definition.maximum_stack_size as "MaximumStackSize",
                definition.unit_weight as "UnitWeight",
                bag.carry_capacity_bonus as "BagCapacityBonus"
            from item_definitions definition
            left join bag_definitions bag on bag.definition_id = definition.id
            where definition.id = any(@DefinitionIds);
            """,
            new { DefinitionIds = RequiredDefinitions.Keys.ToArray() },
            cancellationToken: cancellationToken))).ToDictionary(
                row => row.Id,
                StringComparer.Ordinal);
        foreach (var required in RequiredDefinitions)
        {
            if (!rows.TryGetValue(required.Key, out var row)
                || row.MaximumStackSize != required.Value.MaximumStackSize
                || row.UnitWeight != required.Value.UnitWeight
                || row.BagCapacityBonus != required.Value.BagCapacityBonus)
            {
                throw new InvalidOperationException(
                    $"The Phase 9 fixture requires canonical definition '{required.Key}'.");
            }
        }
    }

    private async Task<Guid> LoadBagContainerIdAsync(
        Guid bagItemInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var value = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select id
            from item_containers
            where bound_bag_item_instance_id = @BagItemInstanceId
              and container_type = 'bag_contents'
              and lifecycle = 'active';
            """,
            new { BagItemInstanceId = bagItemInstanceId },
            cancellationToken: cancellationToken));
        return value ?? throw new InvalidOperationException(
            "The equipped fixture Bag did not activate its contents container.");
    }

    private void EnsureGuardrails(Guid characterId)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The Phase 9 item fixture is available only in the Development environment.");
        }

        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("A character id is required.", nameof(characterId));
        }

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        var host = connection.Host?.Trim() ?? string.Empty;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(host, "::1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Phase 9 item fixture may target only a loopback PostgreSQL host.");
        }

        if (string.IsNullOrWhiteSpace(connection.Database)
            || connection.Database.Contains("prod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Phase 9 item fixture cannot target a production-like database name.");
        }
    }

    private static async Task RequireSuccessAsync(
        Task<ItemTransactionResult> operation,
        string description)
    {
        var result = await operation;
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {description}: {result.Error?.Code}: {result.Error?.Message}");
        }
    }

    private sealed class FixtureTarget
    {
        public Guid CharacterId { get; set; }

        public Guid AccountId { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid SecureContainerId { get; set; }

    }

    private sealed class DefinitionRow
    {
        public string Id { get; set; } = string.Empty;

        public int MaximumStackSize { get; set; }

        public long UnitWeight { get; set; }

        public long? BagCapacityBonus { get; set; }
    }

    private sealed record RequiredDefinition(
        int MaximumStackSize,
        long UnitWeight,
        long? BagCapacityBonus);
}
