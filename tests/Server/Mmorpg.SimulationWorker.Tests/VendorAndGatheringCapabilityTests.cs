using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.SimulationWorker.WorldActors;
using Mmorpg.Testing.Content;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class VendorAndGatheringCapabilityTests
{
    [Fact]
    public async Task OpeningTheVendorConfirmsItWithoutCallingGameService()
    {
        var handler = new VendorWorldActorCapabilityHandler(CreateExecutor(new RecordingHandler()));

        var opened = await handler.ExecuteAsync(
            VendorContext(RealtimeCapabilityActionKind.None),
            CancellationToken.None);

        Assert.True(opened.Succeeded);
        Assert.Contains("Trade", opened.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurchaseSendsTheCapabilityVendorProfileAndTheShownPrice()
    {
        var http = new RecordingHandler();
        var handler = new VendorWorldActorCapabilityHandler(CreateExecutor(http));
        var context = VendorContext(
            RealtimeCapabilityActionKind.VendorPurchase,
            offerDefinitionId: "potion.minor_healing",
            quantity: 2,
            expectedPrice: 50);

        await handler.ExecuteAsync(context, CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Bodies)).RootElement;
        Assert.Equal("vendor_purchase", body.GetProperty("operationKind").GetString());
        Assert.Equal(
            "vendor.city_general_store",
            body.GetProperty("access").GetProperty("vendorProfileId").GetString());
        Assert.Equal("potion.minor_healing", body.GetProperty("definitionId").GetString());
        Assert.Equal(2, body.GetProperty("quantity").GetInt32());
        Assert.Equal(50, body.GetProperty("expectedPrice").GetInt64());
        Assert.Equal(context.Intent.InteractionSessionId, body.GetProperty("interactionSessionId").GetGuid());
    }

    // Arrow batches that commit ahead of the purchase advance the revision it names.
    [Fact]
    public async Task PurchaseSendsTheRebasedCharacterRevision()
    {
        var http = new RecordingHandler();
        var handler = new VendorWorldActorCapabilityHandler(CreateExecutor(http));
        var context = VendorContext(
            RealtimeCapabilityActionKind.VendorPurchase,
            offerDefinitionId: "potion.minor_healing",
            quantity: 1,
            expectedPrice: 25) with
        {
            RebasedCharacterRevision = 12
        };

        await handler.ExecuteAsync(context, CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Bodies)).RootElement;
        Assert.Equal(12, body.GetProperty("expectedCharacterRevision").GetInt64());
        Assert.Equal(0, context.Intent.ExpectedCharacterRevision);
    }

    [Fact]
    public async Task SaleSendsTheItemAndQuantity()
    {
        var http = new RecordingHandler();
        var handler = new VendorWorldActorCapabilityHandler(CreateExecutor(http));
        var itemId = Guid.NewGuid();

        await handler.ExecuteAsync(
            VendorContext(
                RealtimeCapabilityActionKind.VendorSale,
                itemInstanceId: itemId,
                expectedItemRevision: 3,
                quantity: 5,
                expectedPrice: 20),
            CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Bodies)).RootElement;
        Assert.Equal("vendor_sale", body.GetProperty("operationKind").GetString());
        Assert.Equal(itemId, body.GetProperty("itemInstanceId").GetGuid());
        Assert.Equal(3, body.GetProperty("expectedItemRevision").GetInt64());
        Assert.Equal(5, body.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task InsuranceActionsAreRefusedAtTheVendor()
    {
        var http = new RecordingHandler();
        var handler = new VendorWorldActorCapabilityHandler(CreateExecutor(http));

        var result = await handler.ExecuteAsync(
            VendorContext(RealtimeCapabilityActionKind.ApplyInsurance, itemInstanceId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("npc_lifecycle_action_invalid", result.Code);
        Assert.Empty(http.Bodies);
    }

    [Fact]
    public async Task StartGatheringNamesTheNodeOrRejectsADepletedOne()
    {
        var itemStates = new CharacterItemStateStore();
        var handler = GatheringHandler(itemStates);
        var context = GatheringContext(RealtimeCapabilityActionKind.StartGathering);
        Equip(itemStates, context, 1, "tool.starter_pickaxe");

        var started = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.True(started.Succeeded);
        Assert.Same(context.Actor, started.GatheringNode);

        var generation = context.Actor.ResourceGeneration;
        while (context.Actor.TryReserveResourceCharge())
        {
            context.Actor.ConsumeResourceCharge(generation);
        }

        var depleted = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.False(depleted.Succeeded);
        Assert.Equal(GatheringStopCodes.Depleted, depleted.Code);
    }

    [Fact]
    public async Task StartGatheringNeedsTheToolKindOfTheNodeInTheToolSlot()
    {
        var itemStates = new CharacterItemStateStore();
        var handler = GatheringHandler(itemStates);
        var context = GatheringContext(RealtimeCapabilityActionKind.StartGathering);

        var unloaded = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.False(unloaded.Succeeded);
        Assert.Equal("gathering_unavailable", unloaded.Code);

        // An Iron Vein needs a pickaxe: an empty slot and a woodcutting axe never start.
        Equip(itemStates, context, 1);
        var withoutTool = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.False(withoutTool.Succeeded);
        Assert.Equal(GatheringStopCodes.ToolRequired, withoutTool.Code);
        Assert.Equal("Pickaxe required in the Tool slot.", withoutTool.Message);
        Assert.Null(withoutTool.GatheringNode);

        Equip(itemStates, context, 2, "tool.woodcutting_axe");
        var withAxe = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.False(withAxe.Succeeded);
        Assert.Equal(GatheringStopCodes.ToolRequired, withAxe.Code);
        Assert.Equal("Pickaxe required in the Tool slot.", withAxe.Message);

        Equip(itemStates, context, 3, "tool.starter_pickaxe");
        var withPickaxe = await handler.ExecuteAsync(context, CancellationToken.None);
        Assert.True(withPickaxe.Succeeded);
        Assert.Same(context.Actor, withPickaxe.GatheringNode);
    }

    [Fact]
    public async Task GatheringRejectsOtherActions()
    {
        var result = await GatheringHandler(new CharacterItemStateStore()).ExecuteAsync(
            GatheringContext(RealtimeCapabilityActionKind.None),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("gathering_action_invalid", result.Code);
    }

    [Fact]
    public void SummaryCarriesTheCapabilityProfile()
    {
        var context = VendorContext(RealtimeCapabilityActionKind.None);
        var registry = new WorldActorCapabilityRegistry(new DefaultWorldActorCapabilityAvailabilityPolicy());

        var summary = registry.BuildSummary(context.Session, context.Actor);

        Assert.Equal(
            "vendor.city_general_store",
            summary.Capabilities.Single(value => value.Kind == RealtimeWorldActorCapabilityKind.Vendor).ProfileId);
        Assert.All(
            summary.Capabilities.Where(value => value.Kind != RealtimeWorldActorCapabilityKind.Vendor),
            value => Assert.Equal(string.Empty, value.ProfileId));
    }

    private static GatheringWorldActorCapabilityHandler GatheringHandler(CharacterItemStateStore itemStates)
    {
        return new GatheringWorldActorCapabilityHandler(itemStates, ItemCatalogTestData.Compile());
    }

    // Commits the character's equipment at the given item-state revision: the tool, if any.
    private static void Equip(
        CharacterItemStateStore itemStates,
        WorldActorCapabilityOperationContext context,
        long revision,
        string? toolDefinitionId = null)
    {
        var equipment = toolDefinitionId is null
            ? []
            : new[] { new SimulationEquippedItem("tool", Guid.NewGuid(), toolDefinitionId, 1, revision) };
        itemStates.Register(
            context.Session.CharacterId,
            context.Session.SimulationSessionId,
            CharacterStateTestData.ItemState(new PlayerCarryState(revision, 0, 200), equipment),
            out _);
    }

    private static NpcItemLifecycleCapabilityExecutor CreateExecutor(RecordingHandler http)
    {
        return new NpcItemLifecycleCapabilityExecutor(
            new GameServiceClient(new HttpClient(http) { BaseAddress = new Uri("http://game-service.test") }),
            new CharacterItemStateStore(),
            new ActiveSimulationSessionStore());
    }

    private static WorldActorCapabilityOperationContext VendorContext(
        RealtimeCapabilityActionKind action,
        string? offerDefinitionId = null,
        Guid? itemInstanceId = null,
        long expectedItemRevision = 0,
        int quantity = 0,
        long expectedPrice = 0)
    {
        return CreateContext(
            actor => actor.Definition.Id == "npc.city_services",
            WorldActorCapabilityKindIds.Vendor,
            action,
            offerDefinitionId,
            itemInstanceId,
            expectedItemRevision,
            quantity,
            expectedPrice);
    }

    private static WorldActorCapabilityOperationContext GatheringContext(RealtimeCapabilityActionKind action)
    {
        return CreateContext(
            actor => actor.Definition.Kind == WorldActorKindIds.ResourceNode,
            WorldActorCapabilityKindIds.Gathering,
            action);
    }

    private static WorldActorCapabilityOperationContext CreateContext(
        Func<WorldActorRuntimeState, bool> selectActor,
        string capabilityKind,
        RealtimeCapabilityActionKind action,
        string? offerDefinitionId = null,
        Guid? itemInstanceId = null,
        long expectedItemRevision = 0,
        int quantity = 0,
        long expectedPrice = 0)
    {
        var session = RealtimeTestSessions.Create();
        var actor = new WorldActorStore(WorldActorTestData.Compile(), new SimulationEntityRegistry())
            .ActivateAssignment("development-world-1", "local-shard-1", "vendor-runtime")
            .First(selectActor);
        var capability = actor.Definition.Capabilities.Single(value => value.Kind == capabilityKind);
        var intent = RealtimeWorldInteractionIntent.CreateCapabilityAction(
            Guid.NewGuid(),
            session.SimulationSessionId,
            Guid.NewGuid(),
            RealtimeWorldInteractionTargetKind.WorldActor,
            actor.NetworkEntityId,
            actor.RuntimeActorId,
            actor.InteractionRevision,
            capability.Id,
            WorldActorCapabilityRegistry.ToProtocolKind(capability.Kind),
            1,
            action,
            itemInstanceId is null ? 0 : 7,
            itemInstanceId ?? Guid.Empty,
            expectedItemRevision,
            offerDefinitionId: offerDefinitionId,
            quantity: quantity,
            expectedPrice: expectedPrice);
        return new WorldActorCapabilityOperationContext(session, actor, capability, intent);
    }

    // Answers every GameService call with an unavailable problem, recording the bodies.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = JsonContent.Create(new { code = "vendor_will_not_buy", message = "Test." })
            };
        }
    }
}
