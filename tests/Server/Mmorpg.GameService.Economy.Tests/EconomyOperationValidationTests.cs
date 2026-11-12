using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Accounts.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Economy.Tests;

// Pins every answer that the worker item route gives a vendor purchase, a vendor sale, or a
// gathering yield that it refuses before the item transaction or in the command's own checks:
// the HTTP status, the problem code, title, and detail, for one invalid part of the request at a
// time. The route reads JSON, so the cases post JSON as the worker does. A baseline request of each
// kind reaches the item transaction and is rejected there without a change, which proves that
// every refusal comes from the part a case changed, and the accepted cases pin what each kind
// does not check.
public sealed class EconomyOperationValidationTests
{
    private const string WorkerSecret = "economy-validation-worker-secret-at-least-32-characters";
    private const string Vendor = "vendor.fixture_store";
    private const string Incomplete = "The simulation item operation is incomplete or invalid.";
    private const string NotSupported = "The requested in-world item operation is not supported.";
    private const string MalformedDetail = "The request body or parameters are invalid.";
    private const string HttpErrorDetail = "The request could not be completed.";
    private const string DerivedOperationId = "8f14e45f-ceea-567a-9a6f-b3f0a2c4d1e8";

    private static readonly Answer IncompleteAnswer = new(400, "item_simulation_operation_invalid", Incomplete);
    private static readonly Answer NotSupportedAnswer = new(400, "item_simulation_operation_invalid", NotSupported);
    private static readonly Answer MalformedAnswer = new(400, "invalid_request", MalformedDetail);

    // What each baseline gets from the item transaction: no funds, a price the vendor no longer
    // asks, and no pickaxe in the Tool slot.
    private static readonly Answer PurchaseBaseline = new(
        422,
        "currency_insufficient",
        "The character does not have enough currency for this purchase.");

    private static readonly Answer SaleBaseline = new(
        409,
        "vendor_price_changed",
        "The vendor price changed before the sale committed.");

    private static readonly Answer GatherBaseline = new(
        422,
        "gathering_tool_required",
        "A tool of the kind 'pickaxe' is required in the Tool slot.");

    private static readonly string[] Kinds = ["vendor_purchase", "vendor_sale", "gather_resource"];

    [PostgresIntegrationFact]
    public async Task EveryKindKeepsTheChecksOfTheWorkerItemRoute()
    {
        await using var scenario = await Scenario.StartAsync("economy_common");
        var cases = new List<Case>();
        foreach (var kind in Kinds)
        {
            cases.Add(new(kind, "baseline", _ => { }, BaselineOf(kind)));
            cases.Add(new(kind, "another worker", body => body["workerId"] = "another-worker", new(
                403,
                "wrong_simulation_worker",
                "The authenticated simulation worker cannot submit another worker's item operation.")));
            cases.Add(new(kind, "empty session", _ => { }, IncompleteAnswer, Guid.Empty));
            cases.Add(new(kind, "empty operation id", body => body["operationId"] = Guid.Empty, IncompleteAnswer));
            cases.Add(new(kind, "empty account", body => body["accountId"] = Guid.Empty, IncompleteAnswer));
            cases.Add(new(kind, "empty character", body => body["characterId"] = Guid.Empty, IncompleteAnswer));
            cases.Add(new(kind, "negative character revision", body => body["expectedCharacterRevision"] = -1, IncompleteAnswer));
            cases.Add(new(kind, "no worker runtime", body => body.Remove("workerRuntimeId"), IncompleteAnswer));
            cases.Add(new(kind, "invalid shard", body => body["shardId"] = "local shard", IncompleteAnswer));
            cases.Add(new(kind, "no session token", body => body.Remove("sessionToken"), IncompleteAnswer));
            cases.Add(new(kind, "blank session token", body => body["sessionToken"] = "  ", IncompleteAnswer));
            cases.Add(new(kind, "long session token", body => body["sessionToken"] = new string('t', 1025), IncompleteAnswer));
            cases.Add(new(kind, "no access", body => body.Remove("access"), IncompleteAnswer));
            cases.Add(new(kind, "null access", body => body["access"] = null, IncompleteAnswer));
            cases.Add(new(kind, "kind with spaces", body => body["operationKind"] = "  " + kind + "  ", BaselineOf(kind)));
            cases.Add(new(kind, "derived operation id", body => body["operationId"] = DerivedOperationId, new(
                422,
                "item_operation_id_invalid",
                "The item operation id must be a random version 4 UUID.")));
        }

        cases.Add(new("vendor_purchase", "no kind", body => body.Remove("operationKind"), IncompleteAnswer));
        cases.Add(new("vendor_purchase", "long kind", body => body["operationKind"] = new string('k', 65), IncompleteAnswer));
        cases.Add(new("vendor_purchase", "unknown kind", body => body["operationKind"] = "vendor_purchases", NotSupportedAnswer));

        await scenario.AssertAnswersAsync(cases);
    }

    [PostgresIntegrationFact]
    public async Task APurchaseNeedsItsInteractionVendorOfferQuantityAndPrice()
    {
        await using var scenario = await Scenario.StartAsync("economy_purchase");
        const string kind = "vendor_purchase";
        Case Refused(string name, Action<JsonObject> change)
        {
            return new(kind, name, change, NotSupportedAnswer);
        }

        await scenario.AssertAnswersAsync(
        [
            Refused("no interaction", body => body.Remove("interactionSessionId")),
            Refused("empty interaction", body => body["interactionSessionId"] = Guid.Empty),
            Refused("no capability", body => body.Remove("capabilityId")),
            Refused("blank capability", body => body["capabilityId"] = " "),
            Refused("no vendor", body => Access(body).Remove("vendorProfileId")),
            Refused("null vendor", body => Access(body)["vendorProfileId"] = null),
            Refused("blank vendor", body => Access(body)["vendorProfileId"] = "  "),
            Refused("invalid vendor", body => Access(body)["vendorProfileId"] = "vendor fixture"),
            Refused("padded vendor", body => Access(body)["vendorProfileId"] = " " + Vendor),
            Refused("no definition", body => body.Remove("definitionId")),
            Refused("invalid definition", body => body["definitionId"] = "fixture potion"),
            Refused("long definition", body => body["definitionId"] = new string('d', 129)),
            Refused("no quantity", body => body.Remove("quantity")),
            Refused("zero quantity", body => body["quantity"] = 0),
            Refused("negative quantity", body => body["quantity"] = -1),
            Refused("no price", body => body.Remove("expectedPrice")),
            Refused("negative price", body => body["expectedPrice"] = -1),
            new(kind, "unknown vendor", body => Access(body)["vendorProfileId"] = "vendor.missing", new(
                422,
                "vendor_unavailable",
                "The vendor is not available.")),
            new(kind, "not offered", body => body["definitionId"] = "fixture.ore", new(
                422,
                "vendor_offer_unavailable",
                "The vendor does not offer this item.")),
            new(kind, "more than a stack", body => body["quantity"] = 1000, new(
                422,
                "item_stack_limit_exceeded",
                "A purchase quantity must be positive and fit one stack.")),
            new(kind, "another price", body => body["expectedPrice"] = 24, new(
                409,
                "vendor_price_changed",
                "The vendor price changed before the purchase committed.")),
            new(kind, "zero price", body => body["expectedPrice"] = 0, new(
                409,
                "vendor_price_changed",
                "The vendor price changed before the purchase committed.")),
            new(kind, "stale character revision", body => body["expectedCharacterRevision"] = 99, new(
                409,
                "item_state_conflict",
                "Character item state '{character}' changed before the operation committed.")),
            // A purchase reads no gathering field and no item.
            new(kind, "gathering fields", body =>
            {
                body["resourceProfileId"] = "resource profile";
                body["resourceNodeId"] = " ";
                body["requiredToolKind"] = "tool kind";
                body["resourceActorId"] = "actor id";
                body["itemInstanceId"] = Guid.Empty;
            }, PurchaseBaseline),
            new(kind, "quantity and price as strings", body =>
            {
                body["quantity"] = "1";
                body["expectedPrice"] = "24";
            }, new(409, "vendor_price_changed", "The vendor price changed before the purchase committed.")),
            new(kind, "names in Pascal case", body =>
            {
                body.Remove("definitionId");
                body["DefinitionId"] = "fixture.ore";
                Access(body).Remove("vendorProfileId");
                Access(body)["VendorProfileId"] = Vendor;
            }, new(422, "vendor_offer_unavailable", "The vendor does not offer this item.")),
            new(kind, "unknown fields", body =>
            {
                body["vendorId"] = "vendor.other";
                Access(body)["vendor"] = "vendor.other";
            }, PurchaseBaseline)
        ]);
    }

    [PostgresIntegrationFact]
    public async Task ASaleNeedsItsItemInteractionVendorQuantityAndPrice()
    {
        await using var scenario = await Scenario.StartAsync("economy_sale");
        const string kind = "vendor_sale";
        Case Refused(string name, Action<JsonObject> change)
        {
            return new(kind, name, change, NotSupportedAnswer);
        }

        await scenario.AssertAnswersAsync(
        [
            Refused("no item", body => body.Remove("itemInstanceId")),
            Refused("empty item", body => body["itemInstanceId"] = Guid.Empty),
            Refused("no item revision", body => body.Remove("expectedItemRevision")),
            Refused("negative item revision", body => body["expectedItemRevision"] = -1),
            Refused("no interaction", body => body.Remove("interactionSessionId")),
            Refused("empty interaction", body => body["interactionSessionId"] = Guid.Empty),
            Refused("no capability", body => body.Remove("capabilityId")),
            Refused("no vendor", body => Access(body).Remove("vendorProfileId")),
            Refused("invalid vendor", body => Access(body)["vendorProfileId"] = "vendor fixture"),
            Refused("no quantity", body => body.Remove("quantity")),
            Refused("zero quantity", body => body["quantity"] = 0),
            Refused("no price", body => body.Remove("expectedPrice")),
            Refused("negative price", body => body["expectedPrice"] = -1),
            new(kind, "unknown vendor", body => Access(body)["vendorProfileId"] = "vendor.missing", new(
                422,
                "vendor_unavailable",
                "The vendor is not available.")),
            new(kind, "more than the stack", body => body["quantity"] = 6, new(
                409,
                "item_quantity_changed",
                "The sale quantity exceeds the current item quantity.")),
            new(kind, "stale item revision", body => body["expectedItemRevision"] = 99, new(
                409,
                "item_state_conflict",
                "Item '{ore}' changed before the operation committed.")),
            // A sale reads no definition and no gathering field.
            new(kind, "definition and gathering fields", body =>
            {
                body["definitionId"] = "fixture definition";
                body["resourceProfileId"] = "resource profile";
                body["resourceActorId"] = "actor id";
            }, SaleBaseline)
        ]);
    }

    [PostgresIntegrationFact]
    public async Task AGatheringYieldNeedsItsInteractionNodeToolAndYield()
    {
        await using var scenario = await Scenario.StartAsync("economy_gather");
        const string kind = "gather_resource";
        Case Refused(string name, Action<JsonObject> change)
        {
            return new(kind, name, change, NotSupportedAnswer);
        }

        await scenario.AssertAnswersAsync(
        [
            Refused("no interaction", body => body.Remove("interactionSessionId")),
            Refused("empty interaction", body => body["interactionSessionId"] = Guid.Empty),
            Refused("no capability", body => body.Remove("capabilityId")),
            Refused("blank capability", body => body["capabilityId"] = ""),
            Refused("no resource profile", body => body.Remove("resourceProfileId")),
            Refused("invalid resource profile", body => body["resourceProfileId"] = "resource fixture"),
            Refused("no node", body => body.Remove("resourceNodeId")),
            Refused("blank node", body => body["resourceNodeId"] = "   "),
            Refused("no tool kind", body => body.Remove("requiredToolKind")),
            Refused("invalid tool kind", body => body["requiredToolKind"] = "pick axe"),
            Refused("no yield", body => body.Remove("definitionId")),
            Refused("invalid yield", body => body["definitionId"] = "fixture ore"),
            Refused("no quantity", body => body.Remove("quantity")),
            Refused("zero quantity", body => body["quantity"] = 0),
            Refused("invalid resource actor", body => body["resourceActorId"] = "resource iron vein"),
            Refused("blank resource actor", body => body["resourceActorId"] = " "),
            new(kind, "long node", body => body["resourceNodeId"] = new string('n', 257), new(
                422,
                "gathering_yield_invalid",
                "The gathering yield is incomplete or invalid.")),
            // A node id is not an identifier; any text up to 256 characters passes.
            new(kind, "node with spaces", body => body["resourceNodeId"] = " fixture node 1 ", GatherBaseline),
            new(kind, "resource actor", body => body["resourceActorId"] = "resource.iron_vein", GatherBaseline),
            new(kind, "null resource actor", body => body["resourceActorId"] = null, GatherBaseline),
            // A gathering yield reads no vendor, price, or item, and needs no character revision.
            new(kind, "vendor fields", body =>
            {
                Access(body)["vendorProfileId"] = "vendor fixture";
                body["expectedPrice"] = -5;
                body["itemInstanceId"] = Guid.Empty;
                body["expectedCharacterRevision"] = 99;
            }, GatherBaseline)
        ]);
    }

    // A field the route cannot read as its type refuses the whole request, for every kind, as the
    // route's JSON binding does: in Development with an exception that the API pipeline answers.
    [PostgresIntegrationFact]
    public async Task AMalformedFieldRefusesTheRequestInDevelopment()
    {
        var logs = new LogRecorder();
        await using var scenario = await Scenario.StartAsync("economy_malformed", Environments.Development, logs);

        await scenario.AssertAnswersAsync(MalformedCases(MalformedAnswer));
        Assert.All(
            logs.Entries.Where(entry => entry.Level >= LogLevel.Warning),
            entry => Assert.Equal(
                ("Mmorpg.GameService.Platform.Http.ApiExceptionHandler", LogLevel.Warning, "Rejected malformed request {Method} {Path} with correlation id {CorrelationId}."),
                (entry.Category, entry.Level, entry.Template)));
        Assert.Equal(
            MalformedCases(MalformedAnswer).Count,
            logs.Entries.Count(entry => entry.Template == "Rejected malformed request {Method} {Path} with correlation id {CorrelationId}."));
    }

    // Outside Development the binding answers with the bare status, which the status code pages
    // complete, and logs the failure at Debug.
    [PostgresIntegrationFact]
    public async Task AMalformedFieldRefusesTheRequestInProduction()
    {
        var logs = new LogRecorder();
        await using var scenario = await Scenario.StartAsync("economy_malformed_prod", Environments.Production, logs);
        var cases = MalformedCases(new Answer(400, "http_error", HttpErrorDetail));

        await scenario.AssertAnswersAsync(cases);
        var binding = logs.Entries.Where(entry => entry.EventId.Name == "InvalidJsonRequestBody").ToArray();
        Assert.Equal(cases.Count, binding.Length);
        Assert.All(binding, entry =>
        {
            Assert.Equal(
                ("Microsoft.AspNetCore.Http.RequestDelegateFactory", LogLevel.Debug, 2),
                (entry.Category, entry.Level, entry.EventId.Id));
            Assert.Equal(
                "Failed to read parameter \"SimulationItemOperationRequest request\" from the request body as JSON.",
                entry.Message);
            Assert.IsAssignableFrom<JsonException>(entry.Exception);
        });
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    // A committed purchase and sale answer with the worker item response and the character's
    // state afterwards. Ids and times are replaced by placeholders in the order they first appear.
    [PostgresIntegrationFact]
    public async Task CommittedTradesAnswerWithTheWorkerItemResponse()
    {
        await using var scenario = await Scenario.StartAsync("economy_committed");
        await scenario.SetCurrencyAsync(100);

        var purchase = await scenario.PostAsync("vendor_purchase", _ => { });
        var sale = await scenario.PostAsync("vendor_sale", body => body["expectedPrice"] = 4);

        Assert.Equal((200, PurchaseJson), purchase);
        Assert.Equal((200, SaleJson), sale);
    }

    private const string PurchaseJson =
        """
        {"operationId":"<id1>","operationKind":"vendor_purchase","succeeded":true,"error":null,"characterRevisions":[{"characterId":"<id2>","revision":2,"carriedWeight":32,"carryCapacity":200,"currency":75}],"containerRevisions":[{"containerId":"<id3>","revision":2}],"itemRevisions":[{"itemInstanceId":"<id4>","revision":0}],"recoveryDeliveryIds":[],"characterState":{"characterId":"<id2>","itemStateRevision":2,"carriedWeight":32,"carryCapacity":200,"equipment":[]},"itemChanges":[{"itemInstanceId":"<id4>","removed":false,"definitionId":"fixture.potion","quantity":1,"revision":0,"containerId":"<id3>","containerSlotIndex":1,"equippedCharacterId":null,"equipmentSlotId":null,"policies":[],"bagContentsContainerId":null,"bagContentsRevision":null}]}
        """;

    private const string SaleJson =
        """
        {"operationId":"<id1>","operationKind":"vendor_sale","succeeded":true,"error":null,"characterRevisions":[{"characterId":"<id2>","revision":3,"carriedWeight":26,"carryCapacity":200,"currency":79}],"containerRevisions":[{"containerId":"<id3>","revision":3}],"itemRevisions":[{"itemInstanceId":"<id4>","revision":1}],"recoveryDeliveryIds":[],"characterState":{"characterId":"<id2>","itemStateRevision":3,"carriedWeight":26,"carryCapacity":200,"equipment":[]},"itemChanges":[{"itemInstanceId":"<id4>","removed":false,"definitionId":"fixture.ore","quantity":4,"revision":1,"containerId":"<id3>","containerSlotIndex":0,"equippedCharacterId":null,"equipmentSlotId":null,"policies":[],"bagContentsContainerId":null,"bagContentsRevision":null}]}
        """;

    private static List<Case> MalformedCases(Answer answer)
    {
        var cases = new List<Case>();
        foreach (var kind in Kinds)
        {
            cases.Add(new(kind, "definition as a number", body => body["definitionId"] = 5, answer));
            cases.Add(new(kind, "price as text", body => body["expectedPrice"] = "five", answer));
            cases.Add(new(kind, "price as a fraction", body => body["expectedPrice"] = 1.5, answer));
            cases.Add(new(kind, "price as an object", body => body["expectedPrice"] = new JsonObject(), answer));
            cases.Add(new(kind, "resource profile as a number", body => body["resourceProfileId"] = 5, answer));
            cases.Add(new(kind, "node as an array", body => body["resourceNodeId"] = new JsonArray(), answer));
            cases.Add(new(kind, "tool kind as a boolean", body => body["requiredToolKind"] = true, answer));
            cases.Add(new(kind, "resource actor as a number", body => body["resourceActorId"] = 5, answer));
            cases.Add(new(kind, "vendor as a number", body => Access(body)["vendorProfileId"] = 5, answer));
            cases.Add(new(kind, "quantity as text", body => body["quantity"] = "one", answer));
        }

        return cases;
    }

    private static Answer BaselineOf(string kind)
    {
        return kind switch
        {
            "vendor_purchase" => PurchaseBaseline,
            "vendor_sale" => SaleBaseline,
            _ => GatherBaseline
        };
    }

    private static JsonObject Access(JsonObject body)
    {
        return body["access"]!.AsObject();
    }

    // The detail may name the character as {character} and the carried ore as {ore}.
    private sealed record Answer(int Status, string Code, string Detail);

    private sealed record Case(
        string Kind,
        string Name,
        Action<JsonObject> Change,
        Answer Expected,
        Guid? SimulationSessionId = null);

    // One character in the world with five carried ore and no funds, its session, and the worker
    // route on an in-process host.
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly PostgresIntegrationTestContext context;
        private readonly SimulationItemApiTestHost host;
        private readonly ConsumedSimulationJoinTicketResponse session;
        private readonly long characterRevision;
        private readonly Guid oreId;
        private readonly long oreRevision;

        private Scenario(
            PostgresIntegrationTestContext context,
            SimulationItemApiTestHost host,
            ConsumedSimulationJoinTicketResponse session,
            long characterRevision,
            Guid oreId,
            long oreRevision)
        {
            this.context = context;
            this.host = host;
            this.session = session;
            this.characterRevision = characterRevision;
            this.oreId = oreId;
            this.oreRevision = oreRevision;
        }

        public static async Task<Scenario> StartAsync(
            string name,
            string? environmentName = null,
            ILoggerProvider? logs = null)
        {
            var context = await PostgresIntegrationTestContext.CreateAsync();
            try
            {
                var world = new ItemScenarioWorld(context);
                var player = await world.RegisterAsync(name);
                var characterId = player.Character.Id;
                var state = await world.LoadStateAsync(characterId);
                var oreId = await world.GrantAsync(characterId, "fixture.ore", 5, state.PermanentInventoryContainerId, 0);
                var session = await world.JoinAsync(player);
                var revision = (await world.LoadStateAsync(characterId)).Revision;
                var ore = await world.LoadItemAsync(oreId);
                var host = await SimulationItemApiTestHost.StartAsync(
                    context,
                    ItemScenarioWorld.WorkerId,
                    WorkerSecret,
                    environmentName,
                    logs);
                host.Client.DefaultRequestHeaders.Add(AuthenticationConstants.SimulationWorkerIdHeader, ItemScenarioWorld.WorkerId);
                host.Client.DefaultRequestHeaders.Add(AuthenticationConstants.SimulationWorkerSecretHeader, WorkerSecret);
                return new Scenario(context, host, session, revision, oreId, ore.Revision);
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
        }

        public async Task AssertAnswersAsync(IReadOnlyList<Case> cases)
        {
            var mismatches = new List<string>();
            foreach (var @case in cases)
            {
                var body = Baseline(@case.Kind);
                @case.Change(body);
                var path = $"/api/simulation-sessions/{@case.SimulationSessionId ?? session.SimulationSessionId}/item-operations";
                using var response = await host.Client.PostAsync(
                    path,
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
                var actual = await ReadAnswerAsync(response);
                var expected = @case.Expected with
                {
                    Detail = @case.Expected.Detail
                        .Replace("{character}", session.CharacterId.ToString(), StringComparison.Ordinal)
                        .Replace("{ore}", oreId.ToString(), StringComparison.Ordinal)
                };
                var title = ReasonPhrase(expected.Status);
                if (actual != (expected.Status, expected.Code, title, expected.Detail))
                {
                    mismatches.Add(
                        $"{@case.Kind} / {@case.Name}: expected {expected.Status} {expected.Code} '{title}' '{expected.Detail}', "
                        + $"got {actual.Status} {actual.Code} '{actual.Title}' '{actual.Detail}'");
                }
            }

            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
            Assert.NotEmpty(cases);
        }

        public Task SetCurrencyAsync(long currency)
        {
            return new ItemScenarioWorld(context).ExecuteSqlAsync(
                "update characters set currency = @Currency where id = @CharacterId;",
                new { Currency = currency, session.CharacterId });
        }

        // Posts a request of the kind at the character's and the ore's current revisions, and
        // returns the status with the body, its ids and times replaced by placeholders.
        public async Task<(int Status, string Body)> PostAsync(string kind, Action<JsonObject> change)
        {
            var world = new ItemScenarioWorld(context);
            var body = Baseline(kind);
            body["expectedCharacterRevision"] = (await world.LoadStateAsync(session.CharacterId)).Revision;
            if (kind == "vendor_sale")
            {
                body["expectedItemRevision"] = (await world.LoadItemAsync(oreId)).Revision;
            }

            change(body);
            using var response = await host.Client.PostAsync(
                $"/api/simulation-sessions/{session.SimulationSessionId}/item-operations",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            return ((int)response.StatusCode, Placeholders(await response.Content.ReadAsStringAsync()));
        }

        public async ValueTask DisposeAsync()
        {
            await host.DisposeAsync();
            await context.DisposeAsync();
        }

        private static string Placeholders(string json)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            json = Regex.Replace(json, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", match =>
            {
                if (!ids.TryGetValue(match.Value, out var index))
                {
                    index = ids.Count + 1;
                    ids.Add(match.Value, index);
                }

                return "<id" + index + ">";
            });
            return Regex.Replace(json, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?", "<time>");
        }

        // A request of the kind that passes every check of the route and of the command, so the
        // item transaction rejects it only for the baseline reason.
        private JsonObject Baseline(string kind)
        {
            var body = new JsonObject
            {
                ["operationId"] = Guid.NewGuid(),
                ["accountId"] = session.AccountId,
                ["characterId"] = session.CharacterId,
                ["workerId"] = session.WorkerId,
                ["workerRuntimeId"] = session.WorkerRuntimeId,
                ["shardId"] = session.ShardId,
                ["sessionToken"] = session.SimulationSessionToken,
                ["access"] = new JsonObject
                {
                    ["bank"] = false,
                    ["recoveryStorage"] = false,
                    ["insuranceNpc"] = false,
                    ["vendorProfileId"] = kind == "gather_resource" ? null : Vendor
                },
                ["operationKind"] = kind,
                ["expectedCharacterRevision"] = kind == "gather_resource" ? 0 : characterRevision,
                ["interactionSessionId"] = Guid.NewGuid()
            };
            switch (kind)
            {
                case "vendor_purchase":
                    body["capabilityId"] = "services.vendor";
                    body["definitionId"] = "fixture.potion";
                    body["quantity"] = 1;
                    body["expectedPrice"] = 25;
                    break;
                case "vendor_sale":
                    body["capabilityId"] = "services.vendor";
                    body["itemInstanceId"] = oreId;
                    body["expectedItemRevision"] = oreRevision;
                    body["quantity"] = 1;
                    body["expectedPrice"] = 5;
                    break;
                default:
                    body["capabilityId"] = "resource.mine";
                    body["definitionId"] = "fixture.ore";
                    body["quantity"] = 1;
                    body["resourceProfileId"] = "resource.fixture";
                    body["resourceNodeId"] = "fixture.node:0000";
                    body["requiredToolKind"] = "pickaxe";
                    break;
            }

            return body;
        }

        private static async Task<(int Status, string? Code, string? Title, string? Detail)> ReadAnswerAsync(
            HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(text))
            {
                return ((int)response.StatusCode, null, null, null);
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return (
                (int)response.StatusCode,
                root.TryGetProperty("code", out var code) ? code.GetString() : null,
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }

        private static string ReasonPhrase(int status)
        {
            return ReasonPhrases.GetReasonPhrase(status);
        }
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        string? Template,
        Exception? Exception);

    // Records every entry with its category, event, message, and template.
    private sealed class LogRecorder : ILoggerProvider
    {
        private readonly Lock gate = new();
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(this, categoryName);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(LogRecorder recorder, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
                    ? values.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string
                    : null;
                lock (recorder.gate)
                {
                    recorder.entries.Add(new LogEntry(
                        category,
                        logLevel,
                        eventId,
                        formatter(state, exception),
                        template,
                        exception));
                }
            }
        }
    }
}
