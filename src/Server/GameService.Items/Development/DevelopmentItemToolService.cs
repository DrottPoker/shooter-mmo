using Dapper;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.GameService.Platform.Database;
using Npgsql;

namespace Mmorpg.GameService.Items.Development;

internal sealed class DevelopmentItemToolService(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    DevelopmentItemPackageApplier packageApplier,
    DevelopmentItemPackageSource packageSource,
    CorpseService corpseService,
    ICharacterWallet characterWallet,
    ICharacterProgression characterProgression,
    ItemCatalogSource catalogSource,
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
            DevelopmentItemToolCommandKind.Level => await SetLevelAsync(
                command,
                packages,
                cancellationToken),
            DevelopmentItemToolCommandKind.Currency => await GrantCurrencyAsync(
                command,
                packages,
                cancellationToken),
            _ => throw new InvalidOperationException("Unsupported development item tool command.")
        };
    }

    // Experience does not exist yet, so this is how a level requirement is tested.
    private async Task<DevelopmentItemToolResponse> SetLevelAsync(
        DevelopmentItemToolCommand command,
        DevelopmentItemPackageDocument packages,
        CancellationToken cancellationToken)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            catalogSource.RuntimeCatalogPath,
            cancellationToken);
        var maximumLevel = catalog.Progression.MaximumCharacterLevel;
        if (command.Level > maximumLevel)
        {
            throw new InvalidOperationException(
                $"The level must be between 1 and the catalog's maximum level {maximumLevel}.");
        }

        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        EnsureOffline(target);
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            if (!await characterProgression.SetLevelAsync(
                    new PostgresTransactionScope(connection, transaction),
                    target.CharacterId,
                    command.Level,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Character '{target.CharacterId}' does not exist.");
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return new DevelopmentItemToolResponse(
            true,
            $"Set the level of {target.CharacterName} to {command.Level}.",
            await LoadDataAsync(packages, cancellationToken));
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

    // Credits currency through the wallet, so the development grant gets its ledger row
    // like every other balance change.
    private async Task<DevelopmentItemToolResponse> GrantCurrencyAsync(
        DevelopmentItemToolCommand command,
        DevelopmentItemPackageDocument packages,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(command.CharacterId, cancellationToken);
        EnsureOffline(target);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var credit = await characterWallet.CreditAsync(
            new PostgresTransactionScope(connection, transaction),
            target.CharacterId,
            command.Amount,
            new CurrencyChangeSource(CurrencySourceKinds.Development, ToolName.Replace(' ', '_'), null),
            cancellationToken);
        if (!credit.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "The currency grant would exceed the maximum currency balance.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new DevelopmentItemToolResponse(
            true,
            $"Granted {command.Amount} currency to {target.CharacterName}, who now has {credit.After}.",
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
                state.carry_capacity as "CarryCapacity",
                {CharactersSql.CurrencyOf("character.id")} as "Currency"
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
                row.CarryCapacity,
                row.Currency)).ToArray(),
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
                $"Character '{target.CharacterName}' must be offline before the development item tool changes it.");
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

        public long Currency { get; set; }
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
