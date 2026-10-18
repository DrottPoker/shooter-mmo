using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// The GameService side of target combat: batched ammunition consumption, the equipment
// snapshot in worker responses, and combat deaths with pending shots and a killer.
public sealed class CombatItemBoundaryIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private const string Arrow = "fixture.arrow";
    private const string AmmunitionSlot = "ammunition";
    private const string CorpsePresentationKey = "corpse.remains";

    [PostgresIntegrationFact]
    public async Task AmmunitionBatchCommitsOnceAndReturnsTheEquippedState()
    {
        const string workerSecret = "combat-test-worker-secret-at-least-32-characters";
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "archer@example.com",
            "archer_player",
            "Archer Hero");
        var arrowId = await GrantAndEquipAsync(context, player, Arrow, 50, AmmunitionSlot);
        var session = await JoinAsync(context, player);
        var joinedArrow = Assert.Single(session.Equipment);
        Assert.Equal(AmmunitionSlot, joinedArrow.EquipmentSlotId);
        Assert.Equal(arrowId, joinedArrow.ItemInstanceId);
        Assert.Equal(Arrow, joinedArrow.DefinitionId);
        Assert.Equal(50, joinedArrow.Quantity);

        await using var host = await SimulationItemApiTestHost.StartAsync(
            context,
            WorkerId,
            workerSecret);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerIdHeader,
            WorkerId);
        host.Client.DefaultRequestHeaders.Add(
            AuthenticationConstants.SimulationWorkerSecretHeader,
            workerSecret);
        var path = $"/api/simulation-sessions/{session.SimulationSessionId}/ammunition-consumptions";
        var batch = CreateConsumption(
            session,
            session.ItemStateRevision,
            arrowId,
            joinedArrow.Revision,
            5);

        using var committed = await host.Client.PostAsJsonAsync(path, batch);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var first = await committed.Content.ReadFromJsonAsync<SimulationItemOperationResponse>();
        Assert.NotNull(first);
        Assert.Equal(ItemOperationKinds.ConsumeEquippedAmmunition, first.OperationKind);
        var carry = Assert.Single(first.CharacterRevisions);
        var consumedArrow = Assert.Single(first.ItemRevisions);
        Assert.Empty(first.ContainerRevisions);
        Assert.True(carry.Revision > session.ItemStateRevision);
        Assert.True(consumedArrow.Revision > joinedArrow.Revision);
        Assert.NotNull(first.CharacterState);
        Assert.Equal(session.CharacterId, first.CharacterState.CharacterId);
        Assert.Equal(carry.Revision, first.CharacterState.ItemStateRevision);
        var equipped = Assert.Single(first.CharacterState.Equipment);
        Assert.Equal(45, equipped.Quantity);
        Assert.Equal(consumedArrow.Revision, equipped.Revision);

        // A retried batch replays the stored result instead of consuming again.
        using var retried = await host.Client.PostAsJsonAsync(path, batch);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        var replay = await retried.Content.ReadFromJsonAsync<SimulationItemOperationResponse>();
        Assert.Equal(consumedArrow, Assert.Single(replay!.ItemRevisions));
        Assert.Equal(45, (await LoadItemAsync(context, arrowId))!.Quantity);

        // A batch against an older item or character revision consumes nothing.
        using var staleItem = await host.Client.PostAsJsonAsync(
            path,
            batch with { OperationId = Guid.NewGuid(), ExpectedCharacterRevision = carry.Revision });
        Assert.Equal(HttpStatusCode.Conflict, staleItem.StatusCode);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, await ReadProblemCodeAsync(staleItem));
        using var staleCharacter = await host.Client.PostAsJsonAsync(
            path,
            batch with { OperationId = Guid.NewGuid(), ExpectedItemRevision = consumedArrow.Revision });
        Assert.Equal(HttpStatusCode.Conflict, staleCharacter.StatusCode);
        Assert.Equal(
            ItemTransactionErrorCodes.ItemStateConflict,
            await ReadProblemCodeAsync(staleCharacter));
        Assert.Equal(45, (await LoadItemAsync(context, arrowId))!.Quantity);

        // The last shots remove the stack and empty the ammunition slot.
        using var exhausted = await host.Client.PostAsJsonAsync(
            path,
            CreateConsumption(session, carry.Revision, arrowId, consumedArrow.Revision, 45));
        Assert.Equal(HttpStatusCode.OK, exhausted.StatusCode);
        var last = await exhausted.Content.ReadFromJsonAsync<SimulationItemOperationResponse>();
        Assert.NotNull(last);
        Assert.Empty(last.ItemRevisions);
        Assert.Empty(last.CharacterState!.Equipment);
        Assert.Null(await LoadItemAsync(context, arrowId));
    }

    [PostgresIntegrationFact]
    public async Task AmmunitionBatchConsumesOnlyTheCharactersEquippedAmmunition()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "quiver@example.com",
            "quiver_player",
            "Quiver Hero");
        var other = await context.RegisterPlayerAsync(
            "other-quiver@example.com",
            "other_quiver_player",
            "Other Quiver Hero");
        var equippedArrowId = await GrantAndEquipAsync(context, player, Arrow, 10, AmmunitionSlot);
        var swordId = await GrantAndEquipAsync(context, player, "fixture.sword", 1, "main_hand");
        var looseArrowId = await GrantAsync(context, player, Arrow, 30, 0);
        var otherArrowId = await GrantAndEquipAsync(context, other, Arrow, 10, AmmunitionSlot);
        var session = await JoinAsync(context, player);

        var looseArrow = await LoadItemAsync(context, looseArrowId);
        await AssertConsumptionRejectedAsync(
            context,
            session,
            CreateConsumption(session, session.ItemStateRevision, looseArrowId, looseArrow!.Revision, 1),
            ItemTransactionErrorCodes.AmmunitionNotEquipped);
        var sword = await LoadItemAsync(context, swordId);
        await AssertConsumptionRejectedAsync(
            context,
            session,
            CreateConsumption(session, session.ItemStateRevision, swordId, sword!.Revision, 1),
            ItemTransactionErrorCodes.AmmunitionNotEquipped);
        var otherArrow = await LoadItemAsync(context, otherArrowId);
        await AssertConsumptionRejectedAsync(
            context,
            session,
            CreateConsumption(session, session.ItemStateRevision, otherArrowId, otherArrow!.Revision, 1),
            ItemTransactionErrorCodes.ItemNotOwned);
        var equippedArrow = await LoadItemAsync(context, equippedArrowId);
        await AssertConsumptionRejectedAsync(
            context,
            session,
            CreateConsumption(
                session,
                session.ItemStateRevision,
                equippedArrowId,
                equippedArrow!.Revision,
                11),
            ItemTransactionErrorCodes.ItemQuantityChanged);

        var empty = await context.SimulationItemMutationService.ConsumeAmmunitionAsync(
            WorkerId,
            session.SimulationSessionId,
            CreateConsumption(
                session,
                session.ItemStateRevision,
                equippedArrowId,
                equippedArrow.Revision,
                0),
            CancellationToken.None);
        Assert.False(empty.Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, empty.StatusCode);

        Assert.Equal(10, (await LoadItemAsync(context, equippedArrowId))!.Quantity);
        Assert.Equal(30, (await LoadItemAsync(context, looseArrowId))!.Quantity);
        Assert.Equal(10, (await LoadItemAsync(context, otherArrowId))!.Quantity);
    }

    [PostgresIntegrationFact]
    public async Task CombatDeathCommitsPendingShotsAndRecordsTheKiller()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "fallen-archer@example.com",
            "fallen_archer_player",
            "Fallen Archer Hero");
        var arrowId = await GrantAndEquipAsync(context, player, Arrow, 20, AmmunitionSlot);
        var session = await JoinAsync(context, player);
        var arrow = Assert.Single(session.Equipment);
        var request = CreateDeath(session, session.ItemStateRevision) with
        {
            AmmunitionConsumption = new SimulationDeathAmmunitionRequest(arrowId, arrow.Revision, 3),
            Killer = new SimulationDeathKillerRequest(DeathKillerKinds.Mob, "mob.fixture_wolf")
        };

        var death = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);

        Assert.True(death.Succeeded, death.Error?.Message);
        var state = death.Value!.CharacterState;
        Assert.NotNull(state);
        Assert.Equal(death.Value.CharacterRevision.Revision, state.ItemStateRevision);
        Assert.Empty(state.Equipment);
        var corpseArrow = await LoadItemAsync(context, arrowId);
        Assert.Equal(17, corpseArrow!.Quantity);
        Assert.Equal(death.Value.Corpse.CorpseId, corpseArrow.CorpseId);
        Assert.Equal(
            (DeathKillerKinds.Mob, "mob.fixture_wolf"),
            await LoadKillerAsync(context, request.DeathEventId));

        // The retried death event replays without consuming the shots again.
        var replay = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.Equal(death.Value.Corpse.CorpseId, replay.Value!.Corpse.CorpseId);
        Assert.Equal(17, (await LoadItemAsync(context, arrowId))!.Quantity);
    }

    [PostgresIntegrationFact]
    public async Task CombatDeathThatSpendsTheLastShotsLeavesNoAmmunitionOnTheCorpse()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "empty-quiver@example.com",
            "empty_quiver_player",
            "Empty Quiver Hero");
        var killer = await context.RegisterPlayerAsync(
            "duelist@example.com",
            "duelist_player",
            "Duelist Hero");
        var arrowId = await GrantAndEquipAsync(context, player, Arrow, 4, AmmunitionSlot);
        var session = await JoinAsync(context, player);
        var arrow = Assert.Single(session.Equipment);
        var baseRequest = CreateDeath(session, session.ItemStateRevision) with
        {
            AmmunitionConsumption = new SimulationDeathAmmunitionRequest(arrowId, arrow.Revision, 4)
        };

        // Malformed killers and stale pending shots are rejected before anything moves.
        var unknownKind = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            baseRequest with
            {
                OperationId = Guid.NewGuid(),
                Killer = new SimulationDeathKillerRequest("npc", "mob.fixture_wolf")
            },
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, unknownKind.StatusCode);
        var characterNotGuid = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            baseRequest with
            {
                OperationId = Guid.NewGuid(),
                Killer = new SimulationDeathKillerRequest(DeathKillerKinds.Character, "mob.fixture_wolf")
            },
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, characterNotGuid.StatusCode);
        var stale = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            baseRequest with
            {
                OperationId = Guid.NewGuid(),
                AmmunitionConsumption = new SimulationDeathAmmunitionRequest(
                    arrowId,
                    arrow.Revision + 1,
                    4)
            },
            CancellationToken.None);
        Assert.False(stale.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, stale.Error!.Code);
        Assert.Equal(4, (await LoadItemAsync(context, arrowId))!.Quantity);

        // A character killer is stored as the canonical Guid text.
        var death = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            baseRequest with
            {
                Killer = new SimulationDeathKillerRequest(
                    DeathKillerKinds.Character,
                    killer.Character.Id.ToString("B").ToUpperInvariant())
            },
            CancellationToken.None);

        Assert.True(death.Succeeded, death.Error?.Message);
        Assert.Null(await LoadItemAsync(context, arrowId));
        Assert.Empty(death.Value!.CharacterState!.Equipment);
        Assert.Equal(
            (DeathKillerKinds.Character, killer.Character.Id.ToString("D")),
            await LoadKillerAsync(context, baseRequest.DeathEventId));
    }

    private static async Task AssertConsumptionRejectedAsync(
        PostgresIntegrationTestContext context,
        ConsumedSimulationJoinTicketResponse session,
        SimulationAmmunitionConsumptionRequest request,
        string expectedCode)
    {
        var result = await context.SimulationItemMutationService.ConsumeAmmunitionAsync(
            WorkerId,
            session.SimulationSessionId,
            request,
            CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error!.Code);
    }

    private static SimulationAmmunitionConsumptionRequest CreateConsumption(
        ConsumedSimulationJoinTicketResponse session,
        long characterRevision,
        Guid itemId,
        long itemRevision,
        int quantity)
    {
        return new SimulationAmmunitionConsumptionRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            characterRevision,
            itemId,
            itemRevision,
            quantity);
    }

    private static SimulationPlayerDeathRequest CreateDeath(
        ConsumedSimulationJoinTicketResponse session,
        long characterRevision)
    {
        return new SimulationPlayerDeathRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            characterRevision,
            4d,
            0d,
            -2d,
            0d,
            0d,
            0d,
            1d,
            CorpsePresentationKey);
    }

    private static async Task<Guid> GrantAndEquipAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string definitionId,
        int quantity,
        string equipmentSlotId)
    {
        var itemId = await GrantAsync(context, player, definitionId, quantity, 0);
        var snapshot = await GetSnapshotAsync(context, player);
        var item = await LoadItemAsync(context, itemId);
        var equip = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<EquipItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new EquipItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    itemId,
                    item!.Revision,
                    equipmentSlotId)),
            CancellationToken.None);
        Assert.True(equip.Succeeded, equip.Error?.Message);
        return itemId;
    }

    private static async Task<Guid> GrantAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player,
        string definitionId,
        int quantity,
        int slotIndex)
    {
        var snapshot = await GetSnapshotAsync(context, player);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    player.Character.Id,
                    snapshot.ItemStateRevision,
                    definitionId,
                    quantity,
                    snapshot.PermanentInventory.ContainerId,
                    slotIndex)),
            CancellationToken.None);
        Assert.True(grant.Succeeded, grant.Error?.Message);
        return Assert.Single(grant.ItemRevisions).ItemInstanceId;
    }

    private static async Task<CharacterInventorySnapshotResponse> GetSnapshotAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var result = await context.ItemQueryService.GetCharacterInventoryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!;
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            ShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(
                join.Value!.JoinTicket,
                WorkerId,
                RuntimeId,
                ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static async Task<ItemRow?> LoadItemAsync(
        PostgresIntegrationTestContext context,
        Guid itemId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
            """
            select
                item.quantity as "Quantity",
                item.revision as "Revision",
                section.corpse_id as "CorpseId"
            from item_instances item
            left join corpse_sections section on section.container_id = item.container_id
            where item.id = @ItemId;
            """,
            new { ItemId = itemId });
    }

    private static async Task<(string?, string?)> LoadKillerAsync(
        PostgresIntegrationTestContext context,
        Guid deathEventId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<KillerRow>(
            """
            select killer_kind as "KillerKind", killer_id as "KillerId"
            from death_events
            where id = @DeathEventId;
            """,
            new { DeathEventId = deathEventId });
        return (row.KillerKind, row.KillerId);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    private sealed class ItemRow
    {
        public int Quantity { get; set; }

        public long Revision { get; set; }

        public Guid? CorpseId { get; set; }
    }

    private sealed class KillerRow
    {
        public string? KillerKind { get; set; }

        public string? KillerId { get; set; }
    }
}
