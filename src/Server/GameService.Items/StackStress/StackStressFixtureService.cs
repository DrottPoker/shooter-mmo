using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Transactions;
using Mmorpg.GameService.Items.Transactions.Domain;
using Mmorpg.GameService.Platform.Http;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Items.StackStress;

internal sealed record StackStressInventoryFixtureRequest(string Workload, string RunId);

// The response names the fixture definitions, so stress clients never name game content.
internal sealed record StackStressInventoryFixtureResponse(
    string Workload,
    CharacterInventorySnapshotResponse Inventory,
    string InventoryDefinitionId,
    string LootDefinitionId);

internal sealed record StackStressKitRequest(string Kit, string RunId);

// The kit's items lie in the permanent inventory of the response; the bot equips them itself.
internal sealed record StackStressKitResponse(
    string Kit,
    CharacterInventorySnapshotResponse Inventory);

internal sealed record StackStressLootHotspotRequest(
    string RunId,
    Guid CharacterId,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double LifetimeSeconds);

internal sealed record StackStressLootHotspotResponse(
    Guid CorpseId,
    string LootDefinitionId,
    int LootStacks,
    int QuantityPerStack,
    DateTime ExpiresAt);

internal sealed class StackStressFixtureService(
    ItemQueryService itemQueryService,
    ItemTransactionService transactionService,
    CorpseService corpseService,
    StackStressFixtureOptions options)
{
    private const int HotspotLootStacks = 24;

    // More than one, so a bot takes a part of a stack, and far below a full stack, so a deposit
    // finds room.
    private const int HotspotQuantityPerStack = 5;

    public async Task<ServiceResult<StackStressInventoryFixtureResponse>>
        SeedInventoryAsync(
            Guid accountId,
            Guid characterId,
            StackStressInventoryFixtureRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsRunId(request.RunId)
            || request.Workload is not ("inventory" or "loot-hotspot" or "mixed-gameplay"))
        {
            return ServiceResult<StackStressInventoryFixtureResponse>.BadRequest(
                "stack_stress_fixture_invalid",
                "The stack stress inventory fixture request is invalid.");
        }

        var snapshotResult = await itemQueryService.GetCharacterInventoryAsync(
            accountId,
            characterId,
            cancellationToken);
        if (!snapshotResult.Succeeded)
        {
            return ForwardError<CharacterInventorySnapshotResponse,
                StackStressInventoryFixtureResponse>(snapshotResult);
        }

        var snapshot = snapshotResult.Value!;
        if (HasItems(snapshot))
        {
            return ServiceResult<StackStressInventoryFixtureResponse>.Conflict(
                "stack_stress_fixture_not_empty",
                "The stack stress inventory fixture requires a new character with no items or Recovery deliveries.");
        }

        var grants = request.Workload switch
        {
            "inventory" => new[] { new FixtureGrant(options.InventoryDefinitionId, 1, 0) },
            "loot-hotspot" => new[] { new FixtureGrant(options.LootDefinitionId, 1, 0) },
            _ => new[]
            {
                new FixtureGrant(options.LootDefinitionId, 1, 0),
                new FixtureGrant(options.InventoryDefinitionId, 1, 1)
            }
        };

        var expectedRevision = snapshot.ItemStateRevision;
        foreach (var grant in grants)
        {
            var result = await transactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        characterId,
                        expectedRevision,
                        grant.DefinitionId,
                        grant.Quantity,
                        snapshot.PermanentInventory.ContainerId,
                        grant.SlotIndex)),
                cancellationToken);
            if (!result.Succeeded)
            {
                return ServiceResult<StackStressInventoryFixtureResponse>.Conflict(
                    result.Error!.Code,
                    result.Error.Message);
            }

            expectedRevision = result.CharacterRevisions.Single().Revision;
        }

        var finalSnapshot = await itemQueryService.GetCharacterInventoryAsync(
            accountId,
            characterId,
            cancellationToken);
        if (!finalSnapshot.Succeeded)
        {
            return ForwardError<CharacterInventorySnapshotResponse,
                StackStressInventoryFixtureResponse>(finalSnapshot);
        }

        return ServiceResult<StackStressInventoryFixtureResponse>.Ok(
            new StackStressInventoryFixtureResponse(
                request.Workload,
                finalSnapshot.Value!,
                options.InventoryDefinitionId,
                options.LootDefinitionId));
    }

    // Grants a new character the kit of its role into its permanent inventory, one stack per
    // slot from the first, through the same item transactions as every other fixture.
    public async Task<ServiceResult<StackStressKitResponse>> SeedKitAsync(
        Guid accountId,
        Guid characterId,
        StackStressKitRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(request.RunId)
            || string.IsNullOrWhiteSpace(request.Kit)
            || !options.Kits.TryGetValue(request.Kit, out var kit))
        {
            return ServiceResult<StackStressKitResponse>.BadRequest(
                "stack_stress_fixture_invalid",
                "The stack stress kit request is invalid or names no configured kit.");
        }

        var snapshotResult = await itemQueryService.GetCharacterInventoryAsync(
            accountId,
            characterId,
            cancellationToken);
        if (!snapshotResult.Succeeded)
        {
            return ForwardError<CharacterInventorySnapshotResponse, StackStressKitResponse>(
                snapshotResult);
        }

        var snapshot = snapshotResult.Value!;
        if (HasItems(snapshot))
        {
            return ServiceResult<StackStressKitResponse>.Conflict(
                "stack_stress_fixture_not_empty",
                "The stack stress kit requires a new character with no items or Recovery deliveries.");
        }

        var expectedRevision = snapshot.ItemStateRevision;
        for (var index = 0; index < kit.Count; index++)
        {
            var result = await transactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        characterId,
                        expectedRevision,
                        kit[index].DefinitionId,
                        kit[index].Quantity,
                        snapshot.PermanentInventory.ContainerId,
                        index)),
                cancellationToken);
            if (!result.Succeeded)
            {
                return ServiceResult<StackStressKitResponse>.Conflict(
                    result.Error!.Code,
                    result.Error.Message);
            }

            expectedRevision = result.CharacterRevisions.Single().Revision;
        }

        var finalSnapshot = await itemQueryService.GetCharacterInventoryAsync(
            accountId,
            characterId,
            cancellationToken);
        if (!finalSnapshot.Succeeded)
        {
            return ForwardError<CharacterInventorySnapshotResponse, StackStressKitResponse>(
                finalSnapshot);
        }

        return ServiceResult<StackStressKitResponse>.Ok(
            new StackStressKitResponse(request.Kit, finalSnapshot.Value!));
    }

    // The hotspot is the corpse of the fixture's own character: a player's corpse takes
    // deposits, moves, and parts of stacks, which a creature's corpse does not. The character
    // receives the loot and dies through the death transaction of every player, so the corpse
    // lasts as long as every player's, and a run that needs it longer is refused.
    public async Task<ServiceResult<StackStressLootHotspotResponse>>
        CreateLootHotspotAsync(
            Guid accountId,
            StackStressLootHotspotRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsRunId(request.RunId)
            || request.CharacterId == Guid.Empty
            || !Identifiers.IsServiceIdentifier(request.ShardId)
            || !IsFinitePosition(request.PositionX)
            || !IsFinitePosition(request.PositionY)
            || !IsFinitePosition(request.PositionZ)
            || !double.IsFinite(request.LifetimeSeconds)
            || request.LifetimeSeconds < 1)
        {
            return ServiceResult<StackStressLootHotspotResponse>.BadRequest(
                "stack_stress_fixture_invalid",
                "The stack stress loot-hotspot fixture request is invalid.");
        }

        if (request.LifetimeSeconds > CorpseTransformRules.PlayerCorpseLifetime.TotalSeconds)
        {
            return ServiceResult<StackStressLootHotspotResponse>.BadRequest(
                "stack_stress_fixture_invalid",
                $"The loot hotspot is a player's corpse, which lasts {CorpseTransformRules.PlayerCorpseLifetime.TotalSeconds:0} seconds; the run needs it for {request.LifetimeSeconds:0}.");
        }

        var snapshotResult = await itemQueryService.GetCharacterInventoryAsync(
            accountId,
            request.CharacterId,
            cancellationToken);
        if (!snapshotResult.Succeeded)
        {
            return ForwardError<CharacterInventorySnapshotResponse,
                StackStressLootHotspotResponse>(snapshotResult);
        }

        var snapshot = snapshotResult.Value!;
        if (HasItems(snapshot))
        {
            return ServiceResult<StackStressLootHotspotResponse>.Conflict(
                "stack_stress_fixture_not_empty",
                "The stack stress loot-hotspot fixture requires a new character with no items or Recovery deliveries.");
        }

        // As many stacks as the character can carry: it is a player, so its carry weight
        // bounds the stock. The bots deposit before they loot, which keeps the corpse filled.
        var lootStacks = 0;
        var expectedRevision = snapshot.ItemStateRevision;
        var slots = Math.Min(HotspotLootStacks, snapshot.PermanentInventory.Slots.Count);
        for (var slotIndex = 0; slotIndex < slots; slotIndex++)
        {
            var granted = await transactionService.ExecuteAsync(
                new ItemTransactionRequest<GrantItemCommand>(
                    Guid.NewGuid(),
                    ItemTransactionActor.ForSystem(),
                    new GrantItemCommand(
                        request.CharacterId,
                        expectedRevision,
                        options.LootDefinitionId,
                        HotspotQuantityPerStack,
                        snapshot.PermanentInventory.ContainerId,
                        slotIndex)),
                cancellationToken);
            if (!granted.Succeeded
                && lootStacks > 0
                && granted.Error!.Code == ItemTransactionErrorCodes.CarryWeightLimitExceeded)
            {
                break;
            }

            if (!granted.Succeeded)
            {
                return ServiceResult<StackStressLootHotspotResponse>.Conflict(
                    granted.Error!.Code,
                    granted.Error.Message);
            }

            expectedRevision = granted.CharacterRevisions.Single().Revision;
            lootStacks++;
        }

        var death = await corpseService.ProcessSystemDeathAsync(
            Guid.NewGuid(),
            new ProcessPlayerDeathCommand(
                Guid.NewGuid(),
                request.CharacterId,
                expectedRevision,
                request.ShardId,
                request.PositionX,
                request.PositionY,
                request.PositionZ,
                0,
                0,
                0,
                1,
                ItemTransactionService.DefaultPlayerCorpsePresentationKey),
            cancellationToken);
        if (!death.Succeeded)
        {
            return ForwardError<PlayerDeathPartitionResponse, StackStressLootHotspotResponse>(death);
        }

        return ServiceResult<StackStressLootHotspotResponse>.Ok(
            new StackStressLootHotspotResponse(
                death.Value!.Corpse.CorpseId,
                options.LootDefinitionId,
                lootStacks,
                HotspotQuantityPerStack,
                death.Value.Corpse.ExpiresAt));
    }

    private static bool HasItems(CharacterInventorySnapshotResponse snapshot)
    {
        return snapshot.PermanentInventory.Slots.Any(slot => slot.Item is not null)
            || snapshot.Equipment.Any(slot => slot.Item is not null)
            || snapshot.EquippedBag is not null
            || snapshot.Bank.Slots.Any(slot => slot.Item is not null)
            || snapshot.RecoveryStorage.Deliveries.Count > 0;
    }

    private static ServiceResult<TTarget> ForwardError<TSource, TTarget>(
        ServiceResult<TSource> source)
    {
        return new ServiceResult<TTarget>(default, source.Error, source.StatusCode);
    }

    private static bool IsRunId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 12
            && value.All(char.IsAsciiLetterOrDigit);
    }


    private static bool IsFinitePosition(double value)
    {
        return double.IsFinite(value) && Math.Abs(value) <= 1_000_000d;
    }

    private sealed record FixtureGrant(string DefinitionId, int Quantity, int SlotIndex);
}
