using Mmorpg.GameService.Items;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Items;
using static Mmorpg.GameService.Testing.ItemScenarioWorld;

namespace Mmorpg.GameService.Quests.Tests;

// Pins the exact statements, transaction boundaries, stored outcome, and audit journal of the
// quest commands that the item transaction trace goldens cover, as ItemTransactionTraceGoldenTests
// does for the item commands. A refactor must keep every golden file byte-identical.
public sealed class QuestTransactionTraceGoldenTests
{
    private const string Ring = "fixture.ring";
    private const string QuestItem = "fixture.letter";
    private const string QuestGrantId = "golden_quest";

    // The setup is the policy scenario of ItemTransactionTraceGoldenTests up to the quest item's
    // removal, run untraced, so the goldens keep their revisions and carried weight.
    [PostgresIntegrationFact]
    public async Task AbandonQuestItemsMatchesItsGoldenTraces()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        await using var traced = TracedItemTransactions.Create(context, typeof(QuestTransactionTraceGoldenTests).Assembly);
        var player = await world.RegisterAsync("golden_policy");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var inventoryId = state.PermanentInventoryContainerId;
        var protectedRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 0);
        var insuredRingId = await world.GrantAsync(characterId, Ring, 1, inventoryId, 1);
        await world.GrantAsync(
            characterId,
            QuestItem,
            1,
            inventoryId,
            2,
            ItemPolicySourceKinds.QuestGrant,
            QuestGrantId);

        state = await world.LoadStateAsync(characterId);
        var protectedRing = await world.LoadItemAsync(protectedRingId);
        AssertCommitted(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<ApplyItemPolicyCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new ApplyItemPolicyCommand(
                    characterId,
                    state.Revision,
                    protectedRingId,
                    protectedRing.Revision,
                    ItemPolicyIds.ProtectedOnDeath,
                    "integration_fixture",
                    "golden")),
            CancellationToken.None));
        await world.ApplyPolicyAsync(characterId, insuredRingId, ItemPolicyIds.Insured);
        state = await world.LoadStateAsync(characterId);
        var insuredRing = await world.LoadItemAsync(insuredRingId);
        AssertCommitted(await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RemoveInsurancePolicyCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new RemoveInsurancePolicyCommand(characterId, state.Revision, insuredRingId, insuredRing.Revision)),
            CancellationToken.None));

        state = await world.LoadStateAsync(characterId);
        var abandonId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("abandon_quest_items.success", abandonId, service => service.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                abandonId,
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(characterId, state.Revision, QuestGrantId)),
            CancellationToken.None)));

        state = await world.LoadStateAsync(characterId);
        var abandonAgainId = Guid.NewGuid();
        AssertCommitted(await traced.AssertGoldenAsync("abandon_quest_items.already_absent", abandonAgainId, service => service.ExecuteAsync(
            new ItemTransactionRequest<AbandonQuestItemsCommand>(
                abandonAgainId,
                ItemTransactionActor.ForSystem(),
                new AbandonQuestItemsCommand(characterId, state.Revision, QuestGrantId)),
            CancellationToken.None)));
    }
}
