using AuthService.Config;
using AuthService.Http;

namespace AuthService.Items;

public sealed record StackStressInventoryFixtureRequest(string Workload, string RunId);

public sealed record StackStressInventoryFixtureResponse(
    string Workload,
    CharacterInventorySnapshotResponse Inventory);

public sealed record StackStressLootHotspotRequest(
    string RunId,
    string WorkerId,
    string WorkerRuntimeId,
    string ShardId,
    double PositionX,
    double PositionY,
    double PositionZ,
    double LifetimeSeconds);

public sealed record StackStressLootHotspotResponse(
    Guid CorpseId,
    int LootStacks,
    int QuantityPerStack,
    DateTime ExpiresAt);

public sealed class StackStressFixtureService(
    ItemQueryService itemQueryService,
    ItemTransactionService transactionService,
    AuthServiceConfig authServiceConfig)
{
    private const string InventoryDefinitionId = "medical.field_dressing";
    private const string LootDefinitionId = "material.iron_ore";
    private const int HotspotLootStacks = 24;
    private const int HotspotQuantityPerStack = 50;

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
            "inventory" => new[] { new FixtureGrant(InventoryDefinitionId, 1, 0) },
            "loot-hotspot" => new[] { new FixtureGrant(LootDefinitionId, 1, 0) },
            _ => new[]
            {
                new FixtureGrant(LootDefinitionId, 1, 0),
                new FixtureGrant(InventoryDefinitionId, 1, 1)
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
            new StackStressInventoryFixtureResponse(request.Workload, finalSnapshot.Value!));
    }

    public async Task<ServiceResult<StackStressLootHotspotResponse>>
        CreateLootHotspotAsync(
            StackStressLootHotspotRequest request,
            CancellationToken cancellationToken)
    {
        if (!IsRunId(request.RunId)
            || !IsIdentifier(request.WorkerId)
            || !IsIdentifier(request.WorkerRuntimeId)
            || !IsIdentifier(request.ShardId)
            || !IsFinitePosition(request.PositionX)
            || !IsFinitePosition(request.PositionY)
            || !IsFinitePosition(request.PositionZ)
            || !double.IsFinite(request.LifetimeSeconds)
            || request.LifetimeSeconds is < 60 or > 86_400)
        {
            return ServiceResult<StackStressLootHotspotResponse>.BadRequest(
                "stack_stress_fixture_invalid",
                "The stack stress loot-hotspot fixture request is invalid.");
        }

        var corpseId = Guid.NewGuid();
        var loot = Enumerable.Range(0, HotspotLootStacks)
            .Select(_ => new MobCorpseLootEntryRequest(
                Guid.NewGuid(),
                LootDefinitionId,
                HotspotQuantityPerStack))
            .ToArray();
        var result = await transactionService.ExecuteAsync(
            new ItemTransactionRequest<CreatePersistentMobCorpseCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new CreatePersistentMobCorpseCommand(
                    corpseId,
                    request.WorkerId,
                    request.WorkerRuntimeId,
                    request.ShardId,
                    "mob.stack_stress_fixture",
                    $"Stack Stress {request.RunId}",
                    request.PositionX,
                    request.PositionY,
                    request.PositionZ,
                    0,
                    0,
                    0,
                    1,
                    ItemTransactionService.DefaultPlayerCorpsePresentationKey,
                    request.LifetimeSeconds,
                    checked((int)authServiceConfig.SimulationWorkerHeartbeatTimeout.TotalSeconds),
                    loot)),
            cancellationToken);
        if (!result.Succeeded)
        {
            return ServiceResult<StackStressLootHotspotResponse>.Conflict(
                result.Error!.Code,
                result.Error.Message);
        }

        var now = DateTime.UtcNow;
        return ServiceResult<StackStressLootHotspotResponse>.Ok(
            new StackStressLootHotspotResponse(
                corpseId,
                HotspotLootStacks,
                HotspotQuantityPerStack,
                now.AddSeconds(request.LifetimeSeconds)));
    }

    private static bool HasItems(CharacterInventorySnapshotResponse snapshot)
    {
        return snapshot.PermanentInventory.Slots.Any(slot => slot.Item is not null)
            || snapshot.Equipment.Any(slot => slot.Item is not null)
            || snapshot.EquippedBag is not null
            || snapshot.Bank.Slots.Any(slot => slot.Item is not null)
            || snapshot.SecureContainer.Contents.Slots.Any(slot => slot.Item is not null)
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

    private static bool IsIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsFinitePosition(double value)
    {
        return double.IsFinite(value) && Math.Abs(value) <= 1_000_000d;
    }

    private sealed record FixtureGrant(string DefinitionId, int Quantity, int SlotIndex);
}
