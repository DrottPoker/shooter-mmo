using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Every client sees a player's body model, look, visible equipment, and headwear, and the
// player changes its headwear through GameService.
public sealed class RealtimeAppearanceTests
{
    private static readonly SimulationAppearance WovenLook = new(
        "Human_Female",
        [
            new SimulationAppearanceChoice("face", "", "Freckles"),
            new SimulationAppearanceChoice("hair", "", "DarkBrownHair")
        ],
        false);

    [Fact]
    public async Task ASpawnShowsTheLookAndOnlyTheVisibleEquipmentAndTheOwnerGetsItsOwnLook()
    {
        await using var harness = RealtimeServerHarness.Create();
        var dressed = AddCharacter(harness, "Dressed Hero", WovenLook, Equipped("ring_1", "jewelry.copper_ring"), Equipped("main_hand", "item.short_bow"), Equipped("back", "clothing.wool_cloak"), Equipped("head", "clothing.wool_hood"));
        var viewer = harness.AddCharacter("Viewer Hero");
        await harness.StartAsync();

        var owner = await harness.JoinAsync(dressed);
        var dressedEntityId = owner.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => Changes(owner).Any(change => change.EntityId == dressedEntityId), "the owner's own look");
        var watcher = await harness.JoinAsync(viewer);
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == dressedEntityId), "the dressed spawn");

        var own = Changes(owner).Single(change => change.EntityId == dressedEntityId).Appearance;
        var spawn = watcher.Spawns.Single(candidate => candidate.EntityId == dressedEntityId).Appearance;
        foreach (var appearance in new[] { own, spawn })
        {
            Assert.Equal("Human_Female", appearance.BodyModel);
            Assert.Equal(["face=Freckles", "hair=DarkBrownHair"], appearance.Choices.Select(choice => $"{choice.Kind}{choice.Slot}={choice.Option}"));
            // Content order of the visible slots; others see what the hands hold, but not the ring.
            Assert.Equal(["head", "back", "main_hand"], appearance.Equipment.Select(item => item.EquipmentSlotId));
            Assert.False(appearance.HeadwearLowered);
        }
    }

    [Fact]
    public async Task LoweringTheHeadwearReachesTheOwnerAndEveryViewer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var hooded = AddCharacter(harness, "Hooded Hero", WovenLook, Equipped("head", "clothing.wool_hood"));
        var viewer = harness.AddCharacter("Hood Viewer");
        await harness.StartAsync();
        var owner = await harness.JoinAsync(hooded);
        var watcher = await harness.JoinAsync(viewer);
        var hoodedEntityId = owner.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == hoodedEntityId), "the hooded spawn");

        var lower = RealtimeAppearanceIntent.CreateLowerHeadwear(Guid.NewGuid());
        owner.Send(RealtimeProtocol.EncodeAppearanceIntent(lower));
        await harness.PollUntilAsync(
            () => Results(owner).Any(result => result.OperationId == lower.OperationId)
                && Changes(watcher).Any(change => change.EntityId == hoodedEntityId && change.Appearance.HeadwearLowered)
                && Changes(owner).Any(change => change.EntityId == hoodedEntityId && change.Appearance.HeadwearLowered),
            "the lowered hood");

        var result = Results(owner).Single(candidate => candidate.OperationId == lower.OperationId);
        Assert.True(result.Succeeded);
        Assert.Equal(RealtimeAppearanceIntentKind.LowerHeadwear, result.Kind);
        var request = Assert.Single(harness.GameService.AppearanceChanges);
        Assert.Equal("headwear", request.Change);
        Assert.True(request.HeadwearLowered);
        Assert.Equal(hooded.SimulationSessionToken, request.SessionToken);
        Assert.Equal(RealtimeServerHarness.RuntimeId, request.WorkerRuntimeId);
        var lowered = Changes(watcher).Last(change => change.EntityId == hoodedEntityId).Appearance;
        Assert.Equal("clothing.wool_hood", Assert.Single(lowered.Equipment).DefinitionId);
    }

    [Fact]
    public async Task ARefusedChangeAnswersWithTheErrorAndLeavesTheLook()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Refused Look");
        var viewer = harness.AddCharacter("Refused Viewer");
        harness.GameService.OnAppearanceChange = (_, _, _) => Task.FromResult(ScriptedGameServiceHandler.Problem(
            HttpStatusCode.Forbidden,
            "development_look_disabled",
            "Only a Development GameService changes a body model or a look."));
        await harness.StartAsync();
        var owner = await harness.JoinAsync(character);
        var watcher = await harness.JoinAsync(viewer);
        var entityId = owner.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == entityId), "the spawn");

        var intent = RealtimeAppearanceIntent.CreateSetDevelopmentLook(
            Guid.NewGuid(),
            "Human_Female",
            [new RealtimeAppearanceChoice("face", "", "Freckles")]);
        owner.Send(RealtimeProtocol.EncodeAppearanceIntent(intent));
        await harness.PollUntilAsync(() => Results(owner).Any(result => result.OperationId == intent.OperationId), "the refusal");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(200));

        var result = Results(owner).Single(candidate => candidate.OperationId == intent.OperationId);
        Assert.False(result.Succeeded);
        Assert.Equal("development_look_disabled", result.Error!.Code);
        Assert.DoesNotContain(Changes(watcher), change => change.EntityId == entityId);
        var request = Assert.Single(harness.GameService.AppearanceChanges);
        Assert.Equal("development_look", request.Change);
        Assert.Equal("Human_Female", request.BodyModel);
    }

    [Fact]
    public async Task AChangeAnsweredAfterAReconnectLeavesTheLookTheReconnectLoaded()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = AddCharacter(harness, "Late Hood", WovenLook, Equipped("head", "clothing.wool_hood"));
        var viewer = harness.AddCharacter("Late Viewer");
        var gate = new TestGate();
        harness.GameService.OnAppearanceChange = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return ScriptedGameServiceHandler.Json(WovenLook with { HeadwearLowered = request.HeadwearLowered!.Value });
        };
        await harness.StartAsync();
        var first = await harness.JoinAsync(character);
        var watcher = await harness.JoinAsync(viewer);
        var entityId = first.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == entityId), "the spawn");

        first.Send(RealtimeProtocol.EncodeAppearanceIntent(RealtimeAppearanceIntent.CreateLowerHeadwear(Guid.NewGuid())));
        await harness.PollUntilAsync(() => gate.HasEntered, "the held change");
        // The same session reconnects with a new token, and the worker loads the look GameService
        // had then.
        var reconnect = character with { Ticket = "ticket-late-reconnect", SimulationSessionToken = "late-reconnected-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "second");
        gate.Release();
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.True(harness.Looks.TryGet(character.CharacterId, character.SimulationSessionId, out var look, out _));
        Assert.False(look!.Appearance.HeadwearLowered);
        Assert.DoesNotContain(Changes(watcher), change => change.EntityId == entityId && change.Appearance.HeadwearLowered);
        Assert.Empty(Results(second));
    }

    [Fact]
    public async Task ADevelopmentBotShowsALookAndOutfitOfItsOwnAndCannotChangeIt()
    {
        await using var harness = RealtimeServerHarness.Create();
        var bot = harness.AddCharacter("Dressed Bot", isSyntheticBot: true);
        var viewer = harness.AddCharacter("Bot Viewer");
        await harness.StartAsync();
        var botClient = await harness.JoinAsync(bot);
        var watcher = await harness.JoinAsync(viewer);
        var botEntityId = botClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == botEntityId), "the bot spawn");

        var appearance = watcher.Spawns.Single(spawn => spawn.EntityId == botEntityId).Appearance;
        Assert.Contains(appearance.BodyModel, RealtimeServerHarness.AppearanceCatalog.BodyModels);
        Assert.Contains(appearance.Choices, choice => choice.Kind == "face");
        // The fixture catalog has a tunic for the body, which a bot always wears.
        Assert.Contains(appearance.Equipment, item => item.EquipmentSlotId == "body" && item.DefinitionId == "fixture.tunic");

        var lower = RealtimeAppearanceIntent.CreateLowerHeadwear(Guid.NewGuid());
        botClient.Send(RealtimeProtocol.EncodeAppearanceIntent(lower));
        await harness.PollUntilAsync(() => Results(botClient).Any(result => result.OperationId == lower.OperationId), "the bot refusal");
        Assert.Equal("appearance_unavailable", Results(botClient).Single().Error!.Code);
        Assert.Empty(harness.GameService.AppearanceChanges);
    }

    private static RealtimeTestCharacter AddCharacter(
        RealtimeServerHarness harness,
        string name,
        SimulationAppearance appearance,
        params SimulationEquippedItem[] equipment)
    {
        var character = new RealtimeTestCharacter($"ticket-{name}", name)
        {
            Appearance = appearance,
            Equipment = equipment
        };
        harness.GameService.AddCharacter(character);
        return character;
    }

    private static SimulationEquippedItem Equipped(string equipmentSlotId, string definitionId)
    {
        return new SimulationEquippedItem(equipmentSlotId, Guid.NewGuid(), definitionId, 1, 1);
    }

    private static IReadOnlyList<RealtimeEntityAppearanceChanged> Changes(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeEntityAppearanceChanged>(
            RealtimeMessageType.EntityAppearanceChanged,
            RealtimeProtocol.TryDecodeEntityAppearanceChanged);
    }

    private static IReadOnlyList<RealtimeAppearanceIntentResult> Results(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAppearanceIntentResult>(
            RealtimeMessageType.AppearanceIntentResult,
            RealtimeProtocol.TryDecodeAppearanceIntentResult);
    }
}
