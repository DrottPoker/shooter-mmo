using System.Text.Json;
using Dapper;
using Npgsql;

namespace AuthService.Items;

public enum DevelopmentItemToolCommandKind
{
    List = 1,
    Grant = 2,
    Package = 3
}

public enum DevelopmentItemDestination
{
    PermanentInventory = 1,
    Bank = 2,
    SecureContainer = 3
}

public sealed record DevelopmentItemToolCommand(
    DevelopmentItemToolCommandKind Kind,
    Guid CharacterId,
    string DefinitionId,
    int Quantity,
    DevelopmentItemDestination Destination,
    string PackageId);

public static class DevelopmentItemToolCommandParser
{
    public const string ListArgument = "--dev-items-list";
    public const string GrantArgument = "--dev-items-grant";
    public const string PackageArgument = "--dev-items-package";

    private static readonly string[] CommandArguments =
    [
        ListArgument,
        GrantArgument,
        PackageArgument
    ];

    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (arguments is null)
        {
            return false;
        }

        var commandIndexes = arguments
            .Select((argument, index) => new { argument, index })
            .Where(candidate => CommandArguments.Contains(
                candidate.argument,
                StringComparer.Ordinal))
            .ToArray();
        if (commandIndexes.Length == 0)
        {
            return false;
        }

        if (commandIndexes.Length != 1)
        {
            error = "Exactly one development item tool command may be supplied.";
            return true;
        }

        var selected = commandIndexes[0];
        if (string.Equals(selected.argument, ListArgument, StringComparison.Ordinal))
        {
            command = new DevelopmentItemToolCommand(
                DevelopmentItemToolCommandKind.List,
                Guid.Empty,
                string.Empty,
                0,
                DevelopmentItemDestination.PermanentInventory,
                string.Empty);
            return true;
        }

        if (string.Equals(selected.argument, GrantArgument, StringComparison.Ordinal))
        {
            return TryParseGrant(arguments, selected.index, out command, out error);
        }

        return TryParsePackage(arguments, selected.index, out command, out error);
    }

    private static bool TryParseGrant(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 4 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || string.IsNullOrWhiteSpace(arguments[commandIndex + 2])
            || !int.TryParse(arguments[commandIndex + 3], out var quantity)
            || quantity <= 0
            || !TryParseDestination(arguments[commandIndex + 4], out var destination))
        {
            error = $"{GrantArgument} requires <characterGuid> <definitionId> "
                + "<positiveQuantity> <permanent|bank|secure>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Grant,
            characterId,
            arguments[commandIndex + 2].Trim(),
            quantity,
            destination,
            string.Empty);
        return true;
    }

    private static bool TryParsePackage(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 2 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || string.IsNullOrWhiteSpace(arguments[commandIndex + 2]))
        {
            error = $"{PackageArgument} requires <characterGuid> <packageId>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Package,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            arguments[commandIndex + 2].Trim());
        return true;
    }

    private static bool TryParseCharacterId(string value, out Guid characterId)
    {
        return Guid.TryParse(value, out characterId) && characterId != Guid.Empty;
    }

    private static bool TryParseDestination(
        string value,
        out DevelopmentItemDestination destination)
    {
        destination = value switch
        {
            "permanent" => DevelopmentItemDestination.PermanentInventory,
            "bank" => DevelopmentItemDestination.Bank,
            "secure" => DevelopmentItemDestination.SecureContainer,
            _ => default
        };
        return destination != default;
    }
}

public sealed record DevelopmentItemToolResponse(
    bool Success,
    string Message,
    DevelopmentItemToolData? Data);

public sealed record DevelopmentItemToolData(
    IReadOnlyList<DevelopmentItemToolCharacter> Characters,
    IReadOnlyList<DevelopmentItemToolDefinition> Definitions,
    IReadOnlyList<DevelopmentItemToolPackage> Packages);

public sealed record DevelopmentItemToolCharacter(
    Guid CharacterId,
    string CharacterName,
    string AccountUsername,
    bool IsOnline,
    int ItemCount,
    int RecoveryDeliveryCount,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity);

public sealed record DevelopmentItemToolDefinition(
    string DefinitionId,
    string DisplayName,
    string CategoryId,
    int MaximumStackSize,
    long UnitWeight,
    bool SecureContainerEligible);

public sealed record DevelopmentItemToolPackage(
    string PackageId,
    string DisplayName,
    string Description,
    bool RequiresEmptyCharacter);

public static class DevelopmentItemToolProtocol
{
    public const string ResponsePrefix = "SHOOTER_MMO_DEV_ITEMS_JSON:";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(DevelopmentItemToolResponse response)
    {
        return ResponsePrefix + JsonSerializer.Serialize(response, JsonOptions);
    }
}

public sealed class DevelopmentItemToolService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    PhaseNineDevelopmentFixtureSeeder phaseNineFixtureSeeder,
    IHostEnvironment environment)
{
    private const string PhaseNinePackageId = "phase9_full";

    private static readonly IReadOnlyList<DevelopmentItemToolPackage> Packages =
    [
        new(
            PhaseNinePackageId,
            "Phase 9 Full Test Pack",
            "Complete Phase 9 fixture with an equipped Bag, inventory, Bank, Secure Container, and Recovery delivery.",
            true),
        new(
            "equipment",
            "Equipment Test Pack",
            "Bag, rifle, armor, tool, and ring candidates in Permanent Inventory.",
            true),
        new(
            "stack_operations",
            "Stack Split And Merge Pack",
            "Several stackable materials, medical items, and ammunition in the Bank.",
            true),
        new(
            "encumbrance_100",
            "Encumbrance 100% Pack",
            "Exactly 200 weight at the base capacity of 200.",
            true),
        new(
            "encumbrance_140",
            "Encumbrance 140% Pack",
            "Exactly 280 weight at the 140 percent hard cap for base capacity 200.",
            true),
        new(
            "secure_container",
            "Secure Container Pack",
            "Four eligible item stacks that fill the base Secure Container.",
            true),
        new(
            "recovery_delivery",
            "Recovery Delivery Pack",
            "A ready Recovery delivery containing material and medical stacks.",
            true)
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PackageGrant>> PackageGrants =
        new Dictionary<string, IReadOnlyList<PackageGrant>>(StringComparer.Ordinal)
        {
            ["equipment"] =
            [
                new("bag.field_pack", 1, DevelopmentItemDestination.PermanentInventory),
                new("weapon.training_rifle", 1, DevelopmentItemDestination.PermanentInventory),
                new("armor.starter_vest", 1, DevelopmentItemDestination.PermanentInventory),
                new("tool.starter_pickaxe", 1, DevelopmentItemDestination.PermanentInventory),
                new("ring.starter_band", 1, DevelopmentItemDestination.PermanentInventory)
            ],
            ["stack_operations"] =
            [
                new("material.iron_ore", 20, DevelopmentItemDestination.Bank),
                new("material.iron_ore", 16, DevelopmentItemDestination.Bank),
                new("medical.field_dressing", 6, DevelopmentItemDestination.Bank),
                new("medical.field_dressing", 4, DevelopmentItemDestination.Bank),
                new("ammunition.training_556", 35, DevelopmentItemDestination.Bank),
                new("ammunition.training_556", 25, DevelopmentItemDestination.Bank)
            ],
            ["encumbrance_100"] =
            [
                new("material.iron_ore", 33, DevelopmentItemDestination.PermanentInventory),
                new("medical.field_dressing", 1, DevelopmentItemDestination.PermanentInventory)
            ],
            ["encumbrance_140"] =
            [
                new("material.iron_ore", 46, DevelopmentItemDestination.PermanentInventory),
                new("medical.field_dressing", 2, DevelopmentItemDestination.PermanentInventory)
            ],
            ["secure_container"] =
            [
                new("quest_item.signal_transponder", 1, DevelopmentItemDestination.SecureContainer),
                new("ammunition.training_556", 10, DevelopmentItemDestination.SecureContainer),
                new("medical.field_dressing", 2, DevelopmentItemDestination.SecureContainer),
                new("material.iron_ore", 1, DevelopmentItemDestination.SecureContainer)
            ],
            ["recovery_delivery"] =
            [
                new("material.iron_ore", 8, DevelopmentItemDestination.Bank),
                new("medical.field_dressing", 3, DevelopmentItemDestination.Bank)
            ]
        };

    public async Task<DevelopmentItemToolResponse> ExecuteAsync(
        DevelopmentItemToolCommand command,
        CancellationToken cancellationToken)
    {
        EnsureGuardrails();
        return command.Kind switch
        {
            DevelopmentItemToolCommandKind.List => new DevelopmentItemToolResponse(
                true,
                "Loaded local characters, item definitions, and development packages.",
                await LoadDataAsync(cancellationToken)),
            DevelopmentItemToolCommandKind.Grant => await GrantAsync(command, cancellationToken),
            DevelopmentItemToolCommandKind.Package => await ApplyPackageAsync(
                command,
                cancellationToken),
            _ => throw new InvalidOperationException("Unsupported development item tool command.")
        };
    }

    private async Task<DevelopmentItemToolResponse> GrantAsync(
        DevelopmentItemToolCommand command,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        EnsureOffline(target);
        var destinationContainerId = ResolveDestinationContainerId(target, command.Destination);
        var transaction = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    target.CharacterId,
                    null,
                    command.DefinitionId,
                    command.Quantity,
                    destinationContainerId,
                    null)),
            cancellationToken);
        RequireSuccess(transaction, $"grant '{command.DefinitionId}'");

        return new DevelopmentItemToolResponse(
            true,
            $"Granted {command.Quantity} x {command.DefinitionId} to {target.CharacterName}.",
            await LoadDataAsync(cancellationToken));
    }

    private async Task<DevelopmentItemToolResponse> ApplyPackageAsync(
        DevelopmentItemToolCommand command,
        CancellationToken cancellationToken)
    {
        var package = Packages.SingleOrDefault(candidate => string.Equals(
            candidate.PackageId,
            command.PackageId,
            StringComparison.Ordinal));
        if (package is null)
        {
            throw new InvalidOperationException(
                $"Unknown development item package '{command.PackageId}'.");
        }

        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        EnsureOffline(target);
        EnsureEmpty(target);

        if (string.Equals(package.PackageId, PhaseNinePackageId, StringComparison.Ordinal))
        {
            await phaseNineFixtureSeeder.SeedAsync(target.CharacterId, cancellationToken);
        }
        else
        {
            var grants = PackageGrants[package.PackageId];
            await ValidatePackageDefinitionsAsync(grants, cancellationToken);
            var grantedItems = new List<ItemRevisionExpectation>();
            foreach (var grant in grants)
            {
                var transaction = await transactionService.ExecuteAsync(
                    new ItemTransactionRequest<GrantItemCommand>(
                        Guid.NewGuid(),
                        ItemTransactionActor.ForSystem(),
                        new GrantItemCommand(
                            target.CharacterId,
                            null,
                            grant.DefinitionId,
                            grant.Quantity,
                            ResolveDestinationContainerId(target, grant.Destination),
                            null)),
                    cancellationToken);
                RequireSuccess(transaction, $"grant package item '{grant.DefinitionId}'");
                var item = transaction.ItemRevisions.Single();
                grantedItems.Add(new ItemRevisionExpectation(item.ItemInstanceId, item.Revision));
            }

            if (string.Equals(
                    package.PackageId,
                    "recovery_delivery",
                    StringComparison.Ordinal))
            {
                var transaction = await transactionService.ExecuteAsync(
                    new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                        Guid.NewGuid(),
                        ItemTransactionActor.ForSystem(),
                        new AddRecoveryDeliveryCommand(
                            target.CharacterId,
                            null,
                            "development_item_tool",
                            $"development-item-tool:{Guid.NewGuid():N}",
                            DateTime.UtcNow,
                            null,
                            grantedItems)),
                    cancellationToken);
                RequireSuccess(transaction, "create the package Recovery delivery");
            }
        }

        return new DevelopmentItemToolResponse(
            true,
            $"Applied {package.DisplayName} to {target.CharacterName}.",
            await LoadDataAsync(cancellationToken));
    }

    private async Task<DevelopmentItemToolData> LoadDataAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var characters = (await connection.QueryAsync<CharacterRow>(new CommandDefinition(
            """
            select
                character.id as "CharacterId",
                character.name as "CharacterName",
                account.username as "AccountUsername",
                exists (
                    select 1
                    from character_simulation_sessions session
                    where session.character_id = character.id
                      and session.released_at is null
                      and session.expires_at > now()
                ) as "IsOnline",
                (
                    select count(*)::integer
                    from item_instances item
                    left join item_containers container on container.id = item.container_id
                    left join item_instances source_bag
                      on source_bag.id = container.bound_bag_item_instance_id
                    where coalesce(
                            item.equipped_character_id,
                            container.owner_character_id,
                            source_bag.equipped_character_id) = character.id
                ) as "ItemCount",
                (
                    select count(*)::integer
                    from recovery_deliveries delivery
                    where delivery.character_id = character.id
                ) as "RecoveryDeliveryCount",
                state.revision as "ItemStateRevision",
                state.carried_weight as "CarriedWeight",
                state.carry_capacity as "CarryCapacity"
            from characters character
            join accounts account on account.id = character.account_id
            join character_item_states state on state.character_id = character.id
            where character.deleted_at is null
            order by lower(character.name), character.id;
            """,
            cancellationToken: cancellationToken))).ToArray();
        var definitions = (await connection.QueryAsync<DefinitionRow>(new CommandDefinition(
            """
            select
                definition.id as "DefinitionId",
                definition.display_name as "DisplayName",
                definition.category_id as "CategoryId",
                definition.maximum_stack_size as "MaximumStackSize",
                definition.unit_weight as "UnitWeight",
                coalesce(location.is_allowed, false) as "SecureContainerEligible"
            from item_definitions definition
            left join item_definition_location_rules location
              on location.definition_id = definition.id
             and location.location_kind = 'secure_container'
            where definition.is_active
            order by lower(definition.display_name), definition.id;
            """,
            cancellationToken: cancellationToken))).ToArray();

        return new DevelopmentItemToolData(
            characters.Select(row => new DevelopmentItemToolCharacter(
                row.CharacterId,
                row.CharacterName,
                row.AccountUsername,
                row.IsOnline,
                row.ItemCount,
                row.RecoveryDeliveryCount,
                row.ItemStateRevision,
                row.CarriedWeight,
                row.CarryCapacity)).ToArray(),
            definitions.Select(row => new DevelopmentItemToolDefinition(
                row.DefinitionId,
                row.DisplayName,
                row.CategoryId,
                row.MaximumStackSize,
                row.UnitWeight,
                row.SecureContainerEligible)).ToArray(),
            Packages);
    }

    private async Task<TargetRow> LoadTargetAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        if (characterId == Guid.Empty)
        {
            throw new InvalidOperationException("A non-empty character id is required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TargetRow>(new CommandDefinition(
            """
            select
                character.id as "CharacterId",
                character.name as "CharacterName",
                character.account_id as "AccountId",
                state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                state.bank_container_id as "BankContainerId",
                state.secure_container_id as "SecureContainerId",
                exists (
                    select 1
                    from character_simulation_sessions session
                    where session.character_id = character.id
                      and session.released_at is null
                      and session.expires_at > now()
                ) as "IsOnline",
                (
                    select count(*)::integer
                    from item_instances item
                    left join item_containers container on container.id = item.container_id
                    left join item_instances source_bag
                      on source_bag.id = container.bound_bag_item_instance_id
                    where coalesce(
                            item.equipped_character_id,
                            container.owner_character_id,
                            source_bag.equipped_character_id) = character.id
                ) as "ItemCount",
                (
                    select count(*)::integer
                    from recovery_deliveries delivery
                    where delivery.character_id = character.id
                ) as "RecoveryDeliveryCount"
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

    private async Task ValidatePackageDefinitionsAsync(
        IReadOnlyList<PackageGrant> grants,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var maximumStackSizes = (await connection.QueryAsync<StackLimitRow>(new CommandDefinition(
            """
            select
                id as "DefinitionId",
                maximum_stack_size as "MaximumStackSize"
            from item_definitions
            where is_active
              and id = any(@DefinitionIds);
            """,
            new { DefinitionIds = grants.Select(grant => grant.DefinitionId).Distinct().ToArray() },
            cancellationToken: cancellationToken))).ToDictionary(
                row => row.DefinitionId,
                StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (!maximumStackSizes.TryGetValue(grant.DefinitionId, out var definition)
                || grant.Quantity <= 0
                || grant.Quantity > definition.MaximumStackSize)
            {
                throw new InvalidOperationException(
                    $"Development package definition '{grant.DefinitionId}' is not compatible "
                    + "with the active canonical item catalog.");
            }
        }
    }

    private static Guid ResolveDestinationContainerId(
        TargetRow target,
        DevelopmentItemDestination destination)
    {
        return destination switch
        {
            DevelopmentItemDestination.PermanentInventory =>
                target.PermanentInventoryContainerId,
            DevelopmentItemDestination.Bank => target.BankContainerId,
            DevelopmentItemDestination.SecureContainer => target.SecureContainerId,
            _ => throw new InvalidOperationException("Unsupported item grant destination.")
        };
    }

    private static void EnsureOffline(TargetRow target)
    {
        if (target.IsOnline)
        {
            throw new InvalidOperationException(
                $"Character '{target.CharacterName}' must be offline before items are granted.");
        }
    }

    private static void EnsureEmpty(TargetRow target)
    {
        if (target.ItemCount != 0 || target.RecoveryDeliveryCount != 0)
        {
            throw new InvalidOperationException(
                "Development item packages require a character with no items or Recovery deliveries. "
                + "Use the individual item grant for an existing offline character.");
        }
    }

    private void EnsureGuardrails()
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The development item tool is available only in the Development environment.");
        }

        var connection = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        var host = connection.Host?.Trim() ?? string.Empty;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            && !string.Equals(host, "::1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The development item tool may target only a loopback PostgreSQL host.");
        }

        if (string.IsNullOrWhiteSpace(connection.Database)
            || connection.Database.Contains("prod", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The development item tool cannot target a production-like database name.");
        }
    }

    private static void RequireSuccess(ItemTransactionResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {result.Error?.Code}: {result.Error?.Message}");
        }
    }

    private sealed record PackageGrant(
        string DefinitionId,
        int Quantity,
        DevelopmentItemDestination Destination);

    private sealed class CharacterRow
    {
        public Guid CharacterId { get; set; }

        public string CharacterName { get; set; } = string.Empty;

        public string AccountUsername { get; set; } = string.Empty;

        public bool IsOnline { get; set; }

        public int ItemCount { get; set; }

        public int RecoveryDeliveryCount { get; set; }

        public long ItemStateRevision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }
    }

    private sealed class DefinitionRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string CategoryId { get; set; } = string.Empty;

        public int MaximumStackSize { get; set; }

        public long UnitWeight { get; set; }

        public bool SecureContainerEligible { get; set; }
    }

    private sealed class TargetRow
    {
        public Guid CharacterId { get; set; }

        public string CharacterName { get; set; } = string.Empty;

        public Guid AccountId { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid SecureContainerId { get; set; }

        public bool IsOnline { get; set; }

        public int ItemCount { get; set; }

        public int RecoveryDeliveryCount { get; set; }
    }

    private sealed class StackLimitRow
    {
        public string DefinitionId { get; set; } = string.Empty;

        public int MaximumStackSize { get; set; }
    }
}
