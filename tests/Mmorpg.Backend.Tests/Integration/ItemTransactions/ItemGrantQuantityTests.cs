using Mmorpg.GameService.Items;

namespace Mmorpg.Backend.Tests.Integration.ItemTransactions;

// A grant quantity that is not positive is an invalid quantity, not a stack limit.
public sealed class ItemGrantQuantityTests
{
    [PostgresIntegrationFact]
    public async Task GrantQuantityMustBePositiveAndWithinTheStackLimit()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var player = await world.RegisterAsync("grant_quantity");
        var state = await world.LoadStateAsync(player.Character.Id);

        foreach (var quantity in new[] { 0, -3 })
        {
            var rejected = await GrantAsync(context, player.Character.Id, state, quantity);
            Assert.Equal(ItemTransactionErrorCodes.ItemQuantityChanged, rejected.Error!.Code);
            Assert.Equal("A grant quantity must be positive.", rejected.Error.Message);
        }

        var tooLarge = await GrantAsync(context, player.Character.Id, state, 101);
        Assert.Equal(ItemTransactionErrorCodes.ItemStackLimitExceeded, tooLarge.Error!.Code);
        Assert.Equal("The grant quantity is outside the definition stack limit.", tooLarge.Error.Message);
    }

    private static Task<ItemTransactionResult> GrantAsync(
        PostgresIntegrationTestContext context,
        Guid characterId,
        ItemScenarioWorld.ItemStateRow state,
        int quantity)
    {
        return context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<GrantItemCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new GrantItemCommand(
                    characterId,
                    state.Revision,
                    "material.iron_ore",
                    quantity,
                    state.PermanentInventoryContainerId,
                    0)),
            CancellationToken.None);
    }
}
