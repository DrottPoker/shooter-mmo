using Dapper;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Placement.Contracts;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items.Development;

internal sealed record DevelopmentItemPackageResult(
    Guid CharacterId,
    long ItemStateRevision,
    long CarriedWeight,
    long CarryCapacity,
    int GrantedItemCount,
    int RecoveryDeliveryCount);

// Gives an empty offline character one development item package through the item
// transaction service, so every package state obeys the same rules as gameplay.
internal sealed class DevelopmentItemPackageApplier(
    NpgsqlDataSource dataSource,
    ItemTransactionService transactionService,
    ItemCatalogSource catalogSource)
{
    public const string RecoverySourceKind = "development_item_package";

    public async Task<DevelopmentItemPackageResult> ApplyAsync(
        DevelopmentItemPackage package,
        Guid characterId,
        CancellationToken cancellationToken)
    {
        var target = await LoadTargetAsync(characterId, cancellationToken);
        if (target.IsOnline)
        {
            throw new InvalidOperationException(
                "Development item packages require an offline character.");
        }

        if (target.ItemCount != 0 || target.RecoveryDeliveryCount != 0)
        {
            throw new InvalidOperationException(
                "Development item packages require a character with no items or Recovery deliveries. "
                + "Use the individual item grant for an existing offline character.");
        }

        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(
            catalogSource.RuntimeCatalogPath,
            cancellationToken);
        var problems = DevelopmentItemPackagePlanner.FindProblems(package, catalog, target.CarryCapacity);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Package '{package.Id}' does not fit the current item catalog: "
                + string.Join("; ", problems)
                + ". Nothing was granted. Update the package for the current item catalog.");
        }

        Guid? bagContainerId = null;
        var recoveryItems = new List<ItemRevisionExpectation>();
        foreach (var grant in package.Grants)
        {
            switch (grant.Destination)
            {
                case DevelopmentItemPackageDestinations.Inventory:
                    await GrantAsync(target, grant, target.PermanentInventoryContainerId, cancellationToken);
                    break;
                case DevelopmentItemPackageDestinations.Bank:
                    await GrantAsync(target, grant, target.BankContainerId, cancellationToken);
                    break;
                case DevelopmentItemPackageDestinations.Bag:
                    await GrantAsync(
                        target,
                        grant,
                        bagContainerId ?? throw new InvalidOperationException(
                            $"Package '{package.Id}' grants into a Bag before one is equipped."),
                        cancellationToken);
                    break;
                case DevelopmentItemPackageDestinations.Recovery:
                    var recoveryItem = await GrantAsync(target, grant, target.BankContainerId, cancellationToken);
                    recoveryItems.Add(new ItemRevisionExpectation(
                        recoveryItem.ItemInstanceId,
                        recoveryItem.Revision));
                    break;
                case DevelopmentItemPackageDestinations.Equipment:
                    var equipped = await GrantAndEquipAsync(target, grant, cancellationToken);
                    if (string.Equals(grant.EquipmentSlotId, ItemEquipmentSlotIds.Bag, StringComparison.Ordinal))
                    {
                        bagContainerId = await LoadBagContainerIdAsync(equipped, cancellationToken);
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Package '{package.Id}' uses unsupported destination '{grant.Destination}'.");
            }
        }

        if (recoveryItems.Count > 0)
        {
            RequireSuccess(
                await transactionService.ExecuteAsync(
                    new ItemTransactionRequest<AddRecoveryDeliveryCommand>(
                        Guid.NewGuid(),
                        ItemTransactionActor.ForSystem(),
                        new AddRecoveryDeliveryCommand(
                            target.CharacterId,
                            null,
                            RecoverySourceKind,
                            $"{package.Id}:{target.CharacterId:N}",
                            DateTime.UtcNow,
                            null,
                            recoveryItems)),
                    cancellationToken),
                $"create the Recovery delivery of package '{package.Id}'");
        }

        // The planner already checked the load; this guards against the planner and the
        // kernel computing carry state differently.
        var state = await LoadStateAsync(target.CharacterId, cancellationToken);
        if (package.ExpectedLoadPercent is int expectedLoadPercent
            && state.CarriedWeight * 100 != state.CarryCapacity * expectedLoadPercent)
        {
            throw new InvalidOperationException(
                $"Package '{package.Id}' reached {state.CarriedWeight}/{state.CarryCapacity} instead of "
                + $"exactly {expectedLoadPercent} percent. Update the package for the current item catalog.");
        }

        return new DevelopmentItemPackageResult(
            target.CharacterId,
            state.Revision,
            state.CarriedWeight,
            state.CarryCapacity,
            package.Grants.Count,
            state.RecoveryDeliveryCount);
    }

    private async Task<ItemTransactionItemRevision> GrantAsync(
        PackageTarget target,
        DevelopmentItemPackageGrant grant,
        Guid destinationContainerId,
        CancellationToken cancellationToken)
    {
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    target.CharacterId,
                    null,
                    grant.DefinitionId,
                    grant.Quantity,
                    destinationContainerId,
                    grant.SlotIndex)),
            cancellationToken);
        RequireSuccess(result, $"grant {grant.Quantity} x '{grant.DefinitionId}'");
        return result.ItemRevisions.Single();
    }

    private async Task<Guid> GrantAndEquipAsync(
        PackageTarget target,
        DevelopmentItemPackageGrant grant,
        CancellationToken cancellationToken)
    {
        var item = await GrantAsync(target, grant, target.PermanentInventoryContainerId, cancellationToken);
        RequireSuccess(
            await transactionService.ExecuteAsync(
                new ItemTransactionRequest<EquipItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new EquipItemCommand(
                        target.CharacterId,
                        null,
                        item.ItemInstanceId,
                        item.Revision,
                        grant.EquipmentSlotId!)),
                cancellationToken),
            $"equip '{grant.DefinitionId}' into '{grant.EquipmentSlotId}'");
        return item.ItemInstanceId;
    }

    private async Task<Guid> LoadBagContainerIdAsync(
        Guid bagItemInstanceId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                """
                select id
                from item_containers
                where bound_bag_item_instance_id = @BagItemInstanceId
                  and container_type = 'bag_contents'
                  and lifecycle = 'active';
                """,
                new { BagItemInstanceId = bagItemInstanceId },
                cancellationToken: cancellationToken))
            ?? throw new InvalidOperationException("The equipped package Bag has no active contents.");
    }

    private async Task<PackageTarget> LoadTargetAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PackageTarget>(new CommandDefinition(
                $"""
                select
                    character.id as "CharacterId",
                    state.permanent_inventory_container_id as "PermanentInventoryContainerId",
                    state.bank_container_id as "BankContainerId",
                    state.carry_capacity as "CarryCapacity",
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

    private async Task<PackageState> LoadStateAsync(
        Guid characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<PackageState>(new CommandDefinition(
            """
            select
                state.revision as "Revision",
                state.carried_weight as "CarriedWeight",
                state.carry_capacity as "CarryCapacity",
                (
                    select count(*)::integer
                    from recovery_deliveries delivery
                    where delivery.character_id = state.character_id
                ) as "RecoveryDeliveryCount"
            from character_item_states state
            where state.character_id = @CharacterId;
            """,
            new { CharacterId = characterId },
            cancellationToken: cancellationToken));
    }

    private static void RequireSuccess(ItemTransactionResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {operation}: {result.Error?.Code}: {result.Error?.Message}");
        }
    }

    private sealed class PackageTarget
    {
        public Guid CharacterId { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }

        public Guid BankContainerId { get; set; }

        // With no items the capacity is the character's base carry capacity.
        public long CarryCapacity { get; set; }

        public bool IsOnline { get; set; }

        public int ItemCount { get; set; }

        public int RecoveryDeliveryCount { get; set; }
    }

    private sealed class PackageState
    {
        public long Revision { get; set; }

        public long CarriedWeight { get; set; }

        public long CarryCapacity { get; set; }

        public int RecoveryDeliveryCount { get; set; }
    }
}

// SQL shared by the development item tools.
internal static class DevelopmentItemSql
{
    // Items a character owns: equipped, in its own containers, or inside its equipped Bag.
    public static string OwnedItemCount(string characterIdExpression)
    {
        return $"""
            (
                select count(*)::integer
                from item_instances item
                {ItemOwnershipSql.Joins}
                where {ItemOwnershipSql.OwningCharacterId} = {characterIdExpression}
            )
            """;
    }
}
