using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Quests.Tests;

// The items of a quest grant's lineage: a grant of the lineage is idempotent, a protected quest
// item refuses direct destruction, and abandoning the grant removes exactly its items, also from
// Recovery Storage.
public sealed class QuestItemLineageIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task QuestAbandonmentUsesExactGrantLineageAndReacceptIsIdempotent()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);

        var firstGrant = await GrantQuestItemAsync(
            context,
            player.Character.Id,
            state.Revision,
            state.PermanentInventoryContainerId,
            0,
            "quest-alpha");
        var firstItem = Assert.Single(firstGrant.ItemRevisions);
        var currentRevision = Assert.Single(firstGrant.CharacterRevisions).Revision;

        var secondGrant = await GrantQuestItemAsync(
            context,
            player.Character.Id,
            currentRevision,
            state.PermanentInventoryContainerId,
            1,
            "quest-beta");
        currentRevision = Assert.Single(secondGrant.CharacterRevisions).Revision;

        var duplicateGrant = await GrantQuestItemAsync(
            context,
            player.Character.Id,
            currentRevision,
            state.PermanentInventoryContainerId,
            2,
            "quest-alpha");
        AssertSucceeded(duplicateGrant);
        Assert.Empty(duplicateGrant.CharacterRevisions);
        Assert.Equal(1, await CountQuestItemsAsync(context, "quest-alpha"));

        var directDestroy = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<DestroyItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new DestroyItemCommand(
                    player.Character.Id,
                    currentRevision,
                    firstItem.ItemInstanceId,
                    firstItem.Revision,
                    "player_destroyed")),
            CancellationToken.None);
        Assert.False(directDestroy.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, directDestroy.Error!.Code);

        var abandoned = await context.QuestItemService.AbandonAsync(
            Guid.NewGuid(),
            player.Character.Id,
            currentRevision,
            "quest-alpha",
            CancellationToken.None);
        AssertSucceeded(abandoned);
        currentRevision = Assert.Single(abandoned.CharacterRevisions).Revision;
        Assert.Equal(0, await CountQuestItemsAsync(context, "quest-alpha"));
        Assert.Equal(1, await CountQuestItemsAsync(context, "quest-beta"));

        var reacceptOperationId = Guid.NewGuid();
        var reaccepted = await context.QuestItemService.GrantRequiredItemAsync(
            reacceptOperationId,
            player.Character.Id,
            currentRevision,
            "fixture.letter",
            1,
            state.PermanentInventoryContainerId,
            0,
            "quest-alpha",
            CancellationToken.None);
        AssertSucceeded(reaccepted);
        var replayed = await context.QuestItemService.GrantRequiredItemAsync(
            reacceptOperationId,
            player.Character.Id,
            currentRevision,
            "fixture.letter",
            1,
            state.PermanentInventoryContainerId,
            0,
            "quest-alpha",
            CancellationToken.None);
        Assert.True(replayed.Succeeded, replayed.Error?.Message);
        Assert.Equal(reaccepted.OperationId, replayed.OperationId);
        Assert.Equal(
            Assert.Single(reaccepted.ItemRevisions).ItemInstanceId,
            Assert.Single(replayed.ItemRevisions).ItemInstanceId);
        Assert.Equal(1, await CountQuestItemsAsync(context, "quest-alpha"));
    }

    [PostgresIntegrationFact]
    public async Task QuestAbandonInRecoveryLeavesNoEmptyDeliveryOrSlot()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var grant = await GrantQuestItemAsync(
            context,
            player.Character.Id,
            state.Revision,
            state.PermanentInventoryContainerId,
            0,
            "quest-recovery");
        AssertSucceeded(grant);
        var death = await context.CorpseService.ProcessSystemDeathAsync(
            Guid.NewGuid(),
            new ProcessPlayerDeathCommand(
                Guid.NewGuid(),
                player.Character.Id,
                Assert.Single(grant.CharacterRevisions).Revision,
                "local-shard-1",
                0d,
                0d,
                0d,
                0d,
                0d,
                0d,
                1d,
                "corpse.remains"),
            CancellationToken.None);
        Assert.True(death.Succeeded, death.Error?.Message);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        var recoveryContainerId = await connection.ExecuteScalarAsync<Guid>(
            "select recovery_storage_container_id from character_item_states where character_id = @CharacterId;",
            new { CharacterId = player.Character.Id });
        Assert.Equal(1, await CountRecoveryRowsAsync(connection, player.Character.Id, recoveryContainerId));

        var abandoned = await context.QuestItemService.AbandonAsync(
            Guid.NewGuid(),
            player.Character.Id,
            (await LoadStateAsync(context, player.Character.Id)).Revision,
            "quest-recovery",
            CancellationToken.None);
        AssertSucceeded(abandoned);

        Assert.Equal(0, await CountQuestItemsAsync(context, "quest-recovery"));
        Assert.Equal(0, await CountRecoveryRowsAsync(connection, player.Character.Id, recoveryContainerId));
        var recovery = await context.ItemQueryService.GetCharacterRecoveryAsync(
            player.Registration.AccountId,
            player.Character.Id,
            CancellationToken.None);
        Assert.True(recovery.Succeeded, recovery.Error?.Message);
        Assert.Empty(recovery.Value!.RecoveryStorage.Deliveries);
    }

    private static async Task<int> CountRecoveryRowsAsync(
        Npgsql.NpgsqlConnection connection,
        Guid characterId,
        Guid recoveryContainerId)
    {
        var deliveries = await connection.ExecuteScalarAsync<int>(
            "select count(*) from recovery_deliveries where character_id = @CharacterId and claimed_at is null;",
            new { CharacterId = characterId });
        var slots = await connection.ExecuteScalarAsync<int>(
            "select count(*) from item_container_slots where container_id = @ContainerId;",
            new { ContainerId = recoveryContainerId });
        Assert.Equal(deliveries, slots);
        return deliveries;
    }

    private static Task<ItemTransactionResult> GrantQuestItemAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        long expectedCharacterRevision,
        Guid destinationContainerId,
        int slotIndex,
        string questGrantId)
    {
        return context.QuestItemService.GrantRequiredItemAsync(
            Guid.NewGuid(),
            characterId,
            expectedCharacterRevision,
            "fixture.letter",
            1,
            destinationContainerId,
            slotIndex,
            questGrantId,
            CancellationToken.None);
    }

    private static async Task<int> CountQuestItemsAsync(
        PostgresIntegrationTestContext context,
        string questGrantId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<int>(
            """
            select count(*)
            from item_instance_policies
            where policy_kind = 'protected_on_death'
              and status = 'active'
              and source_kind = @SourceKind
              and source_id = @QuestGrantId;
            """,
            new
            {
                QuestGrantId = questGrantId,
                SourceKind = ItemPolicySourceKinds.QuestGrant
            });
    }

    private static async Task<CharacterStateRow> LoadStateAsync(
        PostgresIntegrationTestContext context,
        Guid characterId)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<CharacterStateRow>(
            """
            select
                revision as "Revision",
                permanent_inventory_container_id as "PermanentInventoryContainerId"
            from character_item_states
            where character_id = @CharacterId;
            """,
            new { CharacterId = characterId });
    }

    private static void AssertSucceeded(ItemTransactionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
    }

    private sealed class CharacterStateRow
    {
        public long Revision { get; set; }

        public Guid PermanentInventoryContainerId { get; set; }
    }
}
