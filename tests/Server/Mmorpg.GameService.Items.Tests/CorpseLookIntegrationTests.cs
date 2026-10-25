using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Tests.ItemTransactions;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.GameService.Items.Tests;

// A player corpse shows the dead character's body and look, wearing the items still in its
// equipment section, so the worker can show the body that lies there.
public sealed class CorpseLookIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task ARestoredCorpseWearsTheEquipmentItDiedInWithTheCharactersLook()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateDeadCharacterAsync(world, "dressed");

        var corpse = await RestoreAsync(context, scenario.CorpseId);

        Assert.NotNull(corpse.Look);
        Assert.Equal("Human_Female", corpse.Look.BodyModel);
        Assert.Equal(
            ["face::Freckles", "hair::DarkBrownHair"],
            corpse.Look.Choices.Select(choice => $"{choice.Kind}:{choice.Slot}:{choice.Option}"));
        Assert.Equal(
            ["body=fixture.tunic", "main_hand=fixture.sword"],
            corpse.Look.Equipment.Select(item => $"{item.EquipmentSlotId}={item.DefinitionId}").Order());
        Assert.True(corpse.Look.HeadwearLowered);
    }

    [PostgresIntegrationFact]
    public async Task ALootedGarmentLeavesTheCorpseLookAndADeletedCharacterKeepsItsBody()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var world = new ItemScenarioWorld(context);
        var scenario = await CreateDeadCharacterAsync(world, "looted");
        var looter = await world.RegisterAsync("looter_looted");
        var tunic = await world.LoadItemAsync(scenario.TunicId);

        var looted = await context.ItemTransactionService.ExecuteAsync(
            new ItemTransactionRequest<LootCorpseItemToEquipmentCommand>(
                Guid.NewGuid(),
                ItemTransactionActor.ForSystem(),
                new LootCorpseItemToEquipmentCommand(
                    looter.Character.Id,
                    scenario.CorpseId,
                    scenario.TunicId,
                    tunic.Revision,
                    "body",
                    null,
                    null)),
            CancellationToken.None);
        ItemScenarioWorld.AssertCommitted(looted);
        await context.ExecuteAsync($"update characters set deleted_at = now() where id = '{scenario.SourceId}';");

        var corpse = await RestoreAsync(context, scenario.CorpseId);

        Assert.Equal("Human_Female", corpse.Look!.BodyModel);
        Assert.Equal(
            ["main_hand=fixture.sword"],
            corpse.Look.Equipment.Select(item => $"{item.EquipmentSlotId}={item.DefinitionId}"));
    }

    // A character with a look of its own dies wearing a tunic and holding a sword.
    private static async Task<DeadCharacter> CreateDeadCharacterAsync(ItemScenarioWorld world, string name)
    {
        var source = await world.RegisterAsync("corpse_" + name);
        var sourceId = source.Character.Id;
        await SetLookAsync(world.Context, sourceId);
        var inventoryId = (await world.LoadStateAsync(sourceId)).PermanentInventoryContainerId;
        var tunic = await world.GrantAsync(sourceId, "fixture.tunic", 1, inventoryId, 0);
        var sword = await world.GrantAsync(sourceId, "fixture.sword", 1, inventoryId, 1);
        await world.EquipAsync(sourceId, tunic, "body");
        await world.EquipAsync(sourceId, sword, "main_hand");
        var corpseId = await world.ProcessDeathAsync(sourceId);
        return new DeadCharacter(sourceId, corpseId, tunic);
    }

    private static async Task SetLookAsync(PostgresIntegrationTestContext context, Guid characterId)
    {
        var appearances = context.Services.GetRequiredService<ICharacterAppearances>();
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var change = await appearances.SetDevelopmentLookAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            "Human_Female",
            [new CharacterAppearanceChoice("face", "", "Freckles"), new CharacterAppearanceChoice("hair", "", "DarkBrownHair")],
            CancellationToken.None);
        Assert.NotNull(change.Appearance);
        var lowered = await appearances.SetHeadwearLoweredAsync(
            new PostgresTransactionScope(connection, transaction),
            characterId,
            true,
            CancellationToken.None);
        Assert.NotNull(lowered.Appearance);
        await transaction.CommitAsync();
    }

    private static async Task<DurableCorpseResponse> RestoreAsync(PostgresIntegrationTestContext context, Guid corpseId)
    {
        var restored = await context.CorpseService.ListForWorkerAsync(
            ItemScenarioWorld.WorkerId,
            ItemScenarioWorld.WorkerRuntimeId,
            ItemScenarioWorld.ShardId,
            CancellationToken.None);
        Assert.True(restored.Succeeded, restored.Error?.Message);
        return Assert.Single(restored.Value!.Corpses, corpse => corpse.CorpseId == corpseId);
    }

    private sealed record DeadCharacter(Guid SourceId, Guid CorpseId, Guid TunicId);
}
