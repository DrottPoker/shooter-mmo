using System.Globalization;
using System.Text.Json;
using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Npgsql;

namespace Mmorpg.GameService.Items.Development;

internal enum DevelopmentItemToolCommandKind
{
    List = 1,
    Grant = 2,
    Package = 3,
    Corpse = 4
}

internal enum DevelopmentItemDestination
{
    PermanentInventory = 1,
    Bank = 2,
    SecureContainer = 3
}

internal sealed record DevelopmentItemToolCommand(
    DevelopmentItemToolCommandKind Kind,
    Guid CharacterId,
    string DefinitionId,
    int Quantity,
    DevelopmentItemDestination Destination,
    string PackageId,
    string ShardId = "",
    double PositionX = 0d,
    double PositionY = 0d,
    double PositionZ = 0d);

internal static class DevelopmentItemToolCommandParser
{
    public const string ListArgument = "--dev-items-list";
    public const string GrantArgument = "--dev-items-grant";
    public const string PackageArgument = "--dev-items-package";
    public const string CorpseArgument = "--dev-items-corpse";

    private static readonly string[] CommandArguments =
    [
        ListArgument,
        GrantArgument,
        PackageArgument,
        CorpseArgument
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

        if (string.Equals(selected.argument, CorpseArgument, StringComparison.Ordinal))
        {
            return TryParseCorpse(arguments, selected.index, out command, out error);
        }

        return TryParsePackage(arguments, selected.index, out command, out error);
    }

    private static bool TryParseCorpse(
        IReadOnlyList<string> arguments,
        int commandIndex,
        out DevelopmentItemToolCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (commandIndex + 5 >= arguments.Count
            || !TryParseCharacterId(arguments[commandIndex + 1], out var characterId)
            || !IsIdentifier(arguments[commandIndex + 2])
            || !TryParseFinite(arguments[commandIndex + 3], out var positionX)
            || !TryParseFinite(arguments[commandIndex + 4], out var positionY)
            || !TryParseFinite(arguments[commandIndex + 5], out var positionZ))
        {
            error = $"{CorpseArgument} requires <characterGuid> <shardId> <x> <y> <z>.";
            return true;
        }

        command = new DevelopmentItemToolCommand(
            DevelopmentItemToolCommandKind.Corpse,
            characterId,
            string.Empty,
            0,
            DevelopmentItemDestination.PermanentInventory,
            string.Empty,
            arguments[commandIndex + 2].Trim(),
            positionX,
            positionY,
            positionZ);
        return true;
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

    private static bool TryParseFinite(string value, out double parsed)
    {
        return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out parsed)
            && double.IsFinite(parsed)
            && parsed is >= -1_000_000d and <= 1_000_000d;
    }

    private static bool IsIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}

internal sealed record DevelopmentItemToolResponse(
    bool Success,
    string Message,
    DevelopmentItemToolData? Data);

internal sealed record DevelopmentItemToolData(
    IReadOnlyList<DevelopmentItemToolCharacter> Characters,
    IReadOnlyList<DevelopmentItemToolDefinition> Definitions,
    IReadOnlyList<DevelopmentItemToolPackage> Packages);

internal sealed record DevelopmentItemToolCharacter(
    Guid CharacterId,
    string CharacterName,
    string AccountUsername,
    bool IsOnline,
    int ItemCount,
    int RecoveryDeliveryCount,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity);

internal sealed record DevelopmentItemToolDefinition(
    string DefinitionId,
    string DisplayName,
    string CategoryId,
    int MaximumStackSize,
    long UnitWeight,
    bool SecureContainerEligible);

internal sealed record DevelopmentItemToolPackage(
    string PackageId,
    string DisplayName,
    string Description,
    bool RequiresEmptyCharacter);

internal static class DevelopmentItemToolProtocol
{
    public const string ResponsePrefix = "MMORPG_DEV_ITEMS_JSON:";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Serialize(DevelopmentItemToolResponse response)
    {
        return ResponsePrefix + JsonSerializer.Serialize(response, JsonOptions);
    }
}

internal sealed class DevelopmentItemToolService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    DevelopmentItemPackageApplier packageApplier,
    DevelopmentItemPackageSource packageSource,
    CorpseService corpseService,
    IHostEnvironment environment)
{
    private const string ToolName = "development item tool";

    public async Task<DevelopmentItemToolResponse> ExecuteAsync(
        DevelopmentItemToolCommand command,
        CancellationToken cancellationToken)
    {
        DevelopmentDatabaseGuard.Ensure(environment, dataSource, ToolName);
        var packages = DevelopmentItemPackageLoader.Load(packageSource.PackagesPath);
        return command.Kind switch
        {
            DevelopmentItemToolCommandKind.List => new DevelopmentItemToolResponse(
                true,
                "Loaded local characters, item definitions, and development packages.",
                await LoadDataAsync(packages, cancellationToken)),
            DevelopmentItemToolCommandKind.Grant => await GrantAsync(command, packages, cancellationToken),
            DevelopmentItemToolCommandKind.Package => await ApplyPackageAsync(
                command,
                packages,
                cancellationToken),
            DevelopmentItemToolCommandKind.Corpse => await CreateCorpseAsync(
                command,
                packages,
                cancellationToken),
            _ => throw new InvalidOperationException("Unsupported development item tool command.")
        };
    }

    // An empty source character first receives the corpse seed package of the packages file.
    private async Task<DevelopmentItemToolResponse> CreateCorpseAsync(
        DevelopmentItemToolCommand command,
        DevelopmentItemPackageDocument packages,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        EnsureOffline(target);
        if (target.ItemCount == 0)
        {
            if (target.RecoveryDeliveryCount != 0)
            {
                throw new InvalidOperationException(
                    "An empty corpse fixture character cannot have pending Recovery deliveries.");
            }

            await packageApplier.ApplyAsync(
                FindPackage(packages, packages.CorpseSeedPackageId),
                target.CharacterId,
                cancellationToken);
            target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        }

        var result = await corpseService.ProcessSystemDeathAsync(
            Guid.NewGuid(),
            new ProcessPlayerDeathCommand(
                Guid.NewGuid(),
                target.CharacterId,
                target.ItemStateRevision,
                command.ShardId,
                command.PositionX,
                command.PositionY,
                command.PositionZ,
                0d,
                0d,
                0d,
                1d,
                ItemTransactionService.DefaultPlayerCorpsePresentationKey),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create the corpse fixture: {result.Error?.Code}: {result.Error?.Message}");
        }

        return new DevelopmentItemToolResponse(
            true,
            $"Created corpse {result.Value!.Corpse.CorpseId} for {target.CharacterName} "
                + $"on {command.ShardId} at ({command.PositionX}, {command.PositionY}, {command.PositionZ}). "
                + "Restart SimulationWorker so it restores the durable corpse.",
            await LoadDataAsync(packages, cancellationToken));
    }

    private async Task<DevelopmentItemToolResponse> GrantAsync(
        DevelopmentItemToolCommand command,
        DevelopmentItemPackageDocument packages,
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
            await LoadDataAsync(packages, cancellationToken));
    }

    private async Task<DevelopmentItemToolResponse> ApplyPackageAsync(
        DevelopmentItemToolCommand command,
        DevelopmentItemPackageDocument packages,
        CancellationToken cancellationToken)
    {
        var package = FindPackage(packages, command.PackageId);
        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        var result = await packageApplier.ApplyAsync(package, target.CharacterId, cancellationToken);
        return new DevelopmentItemToolResponse(
            true,
            $"Applied {package.DisplayName} to {target.CharacterName}: carry "
                + $"{result.CarriedWeight}/{result.CarryCapacity} at revision {result.ItemStateRevision}.",
            await LoadDataAsync(packages, cancellationToken));
    }

    private static DevelopmentItemPackage FindPackage(
        DevelopmentItemPackageDocument packages,
        string packageId)
    {
        return packages.Packages.SingleOrDefault(candidate => string.Equals(
                candidate.Id,
                packageId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Unknown development item package '{packageId}'.");
    }

    private async Task<DevelopmentItemToolData> LoadDataAsync(
        DevelopmentItemPackageDocument packages,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var characters = (await connection.QueryAsync<CharacterRow>(new CommandDefinition(
            $"""
            select
                character.id as "CharacterId",
                character.name as "CharacterName",
                {AccountsSql.UsernameOf("character.account_id")} as "AccountUsername",
                {PlacementSql.HasActiveSimulationSession("character.id")} as "IsOnline",
                {DevelopmentItemSql.OwnedItemCount("character.id")} as "ItemCount",
                (
                    select count(*)::integer
                    from recovery_deliveries delivery
                    where delivery.character_id = character.id
                ) as "RecoveryDeliveryCount",
                state.revision as "ItemStateRevision",
                state.carried_weight as "CarriedWeight",
                state.carry_capacity as "CarryCapacity"
            from {CharactersSql.ActiveCharacters} character
            join character_item_states state on state.character_id = character.id
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
            packages.Packages.Select(package => new DevelopmentItemToolPackage(
                package.Id,
                package.DisplayName,
                package.Description,
                true)).ToArray());
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
            $"""
            select
                character.id as "CharacterId",
                character.name as "CharacterName",
                state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                state.bank_container_id as "BankContainerId",
                state.secure_container_id as "SecureContainerId",
                state.revision as "ItemStateRevision",
                {PlacementSql.HasActiveSimulationSession("character.id")} as "IsOnline",
                {DevelopmentItemSql.OwnedItemCount("character.id")} as "ItemCount",
                (
                    select count(*)::integer
                    from recovery_deliveries delivery
                    where delivery.character_id = character.id
                ) as "RecoveryDeliveryCount"
            from {CharactersSql.ActiveCharacters} character
            join character_item_states state on state.character_id = character.id
            where character.id = @CharacterId;
            """,
            new { CharacterId = characterId },
            cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException(
                $"Character '{characterId}' does not have an initialized item state.");
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

    private static void RequireSuccess(ItemTransactionResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {result.Error?.Code}: {result.Error?.Message}");
        }
    }

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

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        public Guid SecureContainerId { get; set; }

        public bool IsOnline { get; set; }

        public int ItemCount { get; set; }

        public int RecoveryDeliveryCount { get; set; }

        public long ItemStateRevision { get; set; }
    }
}
