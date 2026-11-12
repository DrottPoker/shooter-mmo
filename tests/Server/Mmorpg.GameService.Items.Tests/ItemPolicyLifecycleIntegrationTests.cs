using Dapper;
using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;

namespace Mmorpg.GameService.Items.Tests;

public sealed class ItemPolicyLifecycleIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task InsuranceServiceAppliesAndRemovesOneDeathPolicyWithoutReplacingItem()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var state = await LoadStateAsync(context, player.Character.Id);
        var grant = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForAccount(player.Registration.AccountId),
                new GrantItemCommand(
                    player.Character.Id,
                    state.Revision,
                    "fixture.sword",
                    1,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
        AssertSucceeded(grant);
        var item = Assert.Single(grant.ItemRevisions);
        var character = Assert.Single(grant.CharacterRevisions);

        var insured = await context.ItemPolicyService.ApplyInsuranceAsync(
            Guid.NewGuid(),
            player.Character.Id,
            character.Revision,
            item.ItemInstanceId,
            item.Revision,
            "insurance-grant-1",
            CancellationToken.None);
        AssertSucceeded(insured);
        item = Assert.Single(insured.ItemRevisions);
        character = Assert.Single(insured.CharacterRevisions);

        var policy = await LoadPolicyAsync(context, item.ItemInstanceId, ItemPolicyIds.Insured);
        Assert.Equal("active", policy.Status);
        Assert.Equal(ItemPolicySourceKinds.InsuranceService, policy.SourceKind);
        Assert.Equal("insurance-grant-1", policy.SourceId);

        var duplicate = await context.ItemPolicyService.ApplyInsuranceAsync(
            Guid.NewGuid(),
            player.Character.Id,
            character.Revision,
            item.ItemInstanceId,
            item.Revision,
            "insurance-grant-2",
            CancellationToken.None);
        Assert.False(duplicate.Succeeded);
        Assert.Equal(ItemTransactionErrorCodes.ItemPolicyRestricted, duplicate.Error!.Code);

        var removed = await context.ItemPolicyService.RemoveInsuranceAsync(
            Guid.NewGuid(),
            player.Character.Id,
            character.Revision,
            item.ItemInstanceId,
            item.Revision,
            CancellationToken.None);
        AssertSucceeded(removed);

        policy = await LoadPolicyAsync(context, item.ItemInstanceId, ItemPolicyIds.Insured);
        Assert.Equal("removed", policy.Status);
        Assert.Equal(1, policy.Revision);
        await using (var connection = await context.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(
                1,
                await connection.QuerySingleAsync<int>(
                    "select count(*) from item_instances where id = @ItemInstanceId;",
                    new { item.ItemInstanceId }));
        }
    }

    private static async Task<PolicyRow> LoadPolicyAsync(
        PostgresIntegrationTestContext context,
        Guid itemInstanceId,
        string policyKind)
    {
        await using var connection = await context.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync<PolicyRow>(
            """
            select
                status as "Status",
                source_kind as "SourceKind",
                source_id as "SourceId",
                revision as "Revision"
            from item_instance_policies
            where item_instance_id = @ItemInstanceId
              and policy_kind = @PolicyKind
            order by created_at desc
            limit 1;
            """,
            new { ItemInstanceId = itemInstanceId, PolicyKind = policyKind });
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

    private sealed class PolicyRow
    {
        public string Status { get; set; } = string.Empty;

        public string SourceKind { get; set; } = string.Empty;

        public string SourceId { get; set; } = string.Empty;

        public long Revision { get; set; }
    }
}
