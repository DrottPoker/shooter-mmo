using Dapper;
using Microsoft.AspNetCore.Http;
using Mmorpg.GameService.Accounts;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Items.Tests;

// Once the account session of a simulation session ended, the session goes on for the character's
// body: its spent arrows and its death still commit, while every item operation and corpse view
// that a client asks for is refused with the code of the end, so a client that still holds only
// the old account session changes nothing.
public sealed class AccountSessionEndItemIntegrationTests
{
    private const string WorkerId = "local-simulation-worker-1";
    private const string RuntimeId = "integration-worker-runtime";
    private const string ShardId = "local-shard-1";
    private const string Arrow = "fixture.arrow";

    [PostgresIntegrationFact]
    public async Task AfterALogoutTheBodysWorkCommitsAndAClientsOperationIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("ended-archer@example.com", "ended_archer", "Ended Archer");
        var ringId = await GrantAsync(context, player, "fixture.ring", 1, 1);
        var arrowId = await GrantAndEquipAsync(context, player, Arrow, 20, "ammunition");
        var session = await JoinAsync(context, player);
        var arrow = Assert.Single(session.Equipment);
        var ring = await LoadItemAsync(context, ringId);
        var inventory = (await GetSnapshotAsync(context, player)).PermanentInventory.ContainerId;
        var logout = await context.SessionService.RevokeAsync(
            player.Registration.AccountId,
            player.Registration.SessionId,
            AccountSessionRevocationReason.Logout,
            CancellationToken.None);
        Assert.True(logout.Succeeded, logout.Error?.Message);

        var relocate = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            Relocate(session, session.ItemStateRevision, ringId, ring!.Revision, inventory, 2),
            CancellationToken.None);
        var corpseView = await context.CorpseService.OpenForSimulationAsync(
            WorkerId,
            session.SimulationSessionId,
            Guid.NewGuid(),
            new SimulationCorpseOpenRequest(
                session.AccountId,
                session.CharacterId,
                WorkerId,
                RuntimeId,
                ShardId,
                session.SimulationSessionToken),
            CancellationToken.None);

        Assert.Equal(
            (StatusCodes.Status401Unauthorized, ItemTransactionErrorCodes.AccountSessionEnded),
            (relocate.StatusCode, relocate.Error!.Code));
        Assert.Equal(
            (StatusCodes.Status401Unauthorized, ItemTransactionErrorCodes.AccountSessionEnded),
            (corpseView.StatusCode, corpseView.Error!.Code));
        Assert.Equal(1, (await LoadItemAsync(context, ringId))!.SlotIndex);

        var arrows = await context.SimulationItemMutationService.ConsumeAmmunitionAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationAmmunitionConsumptionRequest(
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                WorkerId,
                RuntimeId,
                ShardId,
                session.SimulationSessionToken,
                session.ItemStateRevision,
                arrowId,
                arrow.Revision,
                5),
            CancellationToken.None);
        Assert.True(arrows.Succeeded, arrows.Error?.Message);
        Assert.Equal(15, (await LoadItemAsync(context, arrowId))!.Quantity);

        var death = await context.CorpseService.ProcessSimulationDeathAsync(
            WorkerId,
            session.SimulationSessionId,
            new SimulationPlayerDeathRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                session.AccountId,
                session.CharacterId,
                WorkerId,
                RuntimeId,
                ShardId,
                session.SimulationSessionToken,
                arrows.Value!.CharacterRevisions.Single().Revision,
                4d,
                0d,
                -2d,
                0d,
                0d,
                0d,
                1d,
                "corpse.remains"),
            CancellationToken.None);
        Assert.True(death.Succeeded, death.Error?.Message);
        Assert.Equal(death.Value!.Corpse.CorpseId, (await LoadItemAsync(context, ringId))!.CorpseId);
    }

    [PostgresIntegrationFact]
    public async Task AfterANewLoginAClientsOperationIsRefusedAsReplaced()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync("replaced-holder@example.com", "replaced_holder", "Replaced Holder");
        var ringId = await GrantAsync(context, player, "fixture.ring", 1, 1);
        var session = await JoinAsync(context, player);
        var ring = await LoadItemAsync(context, ringId);
        var inventory = (await GetSnapshotAsync(context, player)).PermanentInventory.ContainerId;
        var login = await context.AccountService.LoginAsync(
            new LoginAccountRequest("replaced_holder", "TestPass123!"),
            CancellationToken.None);
        Assert.True(login.Succeeded, login.Error?.Message);

        var relocate = await context.SimulationItemMutationService.ExecuteAsync(
            WorkerId,
            session.SimulationSessionId,
            Relocate(session, session.ItemStateRevision, ringId, ring!.Revision, inventory, 2),
            CancellationToken.None);

        Assert.Equal(
            (StatusCodes.Status401Unauthorized, ItemTransactionErrorCodes.AccountSessionReplaced),
            (relocate.StatusCode, relocate.Error!.Code));
        Assert.Equal(1, (await LoadItemAsync(context, ringId))!.SlotIndex);
    }

    private static SimulationItemOperationRequest Relocate(
        ConsumedSimulationJoinTicketResponse session,
        long characterRevision,
        Guid itemId,
        long itemRevision,
        Guid destinationContainerId,
        int destinationSlotIndex)
    {
        return new SimulationItemOperationRequest(
            Guid.NewGuid(),
            session.AccountId,
            session.CharacterId,
            session.WorkerId,
            session.WorkerRuntimeId,
            session.ShardId,
            session.SimulationSessionToken,
            new SimulationItemAccessRequest(false, false, false),
            ItemOperationKinds.Relocate,
            characterRevision,
            itemId,
            itemRevision,
            DestinationContainerId: destinationContainerId,
            DestinationSlotIndex: destinationSlotIndex);
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
                new EquipItemCommand(player.Character.Id, snapshot.ItemStateRevision, itemId, item!.Revision, equipmentSlotId)),
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
            new ConsumeSimulationJoinTicketRequest(join.Value!.JoinTicket, WorkerId, RuntimeId, ShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static async Task<ItemRow?> LoadItemAsync(PostgresIntegrationTestContext context, Guid itemId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ItemRow>(
            """
            select
                item.quantity as "Quantity",
                item.revision as "Revision",
                item.container_slot_index as "SlotIndex",
                section.corpse_id as "CorpseId"
            from item_instances item
            left join corpse_sections section on section.container_id = item.container_id
            where item.id = @ItemId;
            """,
            new { ItemId = itemId });
    }

    private sealed class ItemRow
    {
        public int Quantity { get; set; }

        public long Revision { get; set; }

        public int? SlotIndex { get; set; }

        public Guid? CorpseId { get; set; }
    }
}
