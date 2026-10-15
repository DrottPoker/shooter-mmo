using Mmorpg.GameService.Items;
using static Mmorpg.Backend.Tests.Integration.ItemTransactions.ItemScenarioWorld;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// Account and simulation worker actors must name the character revision they expect,
// and the rejection names the authority that omitted it.
public sealed class ExpectedCharacterRevisionTests
{
    [PostgresIntegrationFact]
    public async Task MissingExpectedRevisionNamesTheActorAuthority()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("expected_revision");
        var characterId = player.Character.Id;
        var state = await world.LoadStateAsync(characterId);
        var itemId = await world.GrantAsync(characterId, "material.iron_ore", 1, state.PermanentInventoryContainerId, 0);
        var item = await world.LoadItemAsync(itemId);
        var command = new RelocateItemCommand(
            characterId,
            null,
            itemId,
            item.Revision,
            state.PermanentInventoryContainerId,
            1);

        var account = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForOfflineAccount(player.Registration.AccountId),
                command),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, account.Error!.Code);
        Assert.Equal(
            "Account-authorized item operations require an expected character revision.",
            account.Error.Message);

        var session = await world.JoinAsync(player);
        var simulation = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<RelocateItemCommand>(
                Guid.NewGuid(),
                SimulationActor(session),
                command),
            CancellationToken.None);
        Assert.Equal(ItemTransactionErrorCodes.ItemStateConflict, simulation.Error!.Code);
        Assert.Equal(
            "Simulation-authorized item operations require an expected character revision.",
            simulation.Error.Message);
    }
}
