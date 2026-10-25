using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.ActiveSimulationBots;

namespace Mmorpg.Tools.Tests;

public sealed class ActiveSimulationBotAuthorityClientTests
{
    [Fact]
    public async Task AcceptsACompleteTicketForTheRequestedShard()
    {
        var result = await IssueAsync(Ticket());

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(27016, result.Ticket!.Endpoint.UdpPort);
    }

    [Fact]
    public async Task ATicketWithoutAnEndpointIsARejectionInsteadOfACrash()
    {
        var ticket = Ticket();
        ticket.Remove("endpoint");

        var result = await IssueAsync(ticket);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_authority_response", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsATicketForAnotherShard()
    {
        var ticket = Ticket();
        ticket["shardId"] = "local-shard-1";

        var result = await IssueAsync(ticket);

        Assert.Equal("invalid_authority_response", result.ErrorCode);
    }

    [Fact]
    public async Task NeverConnectsToAWorkerOnAnotherProtocolVersion()
    {
        var ticket = Ticket();
        ticket["endpoint"]!["protocolVersion"] = RealtimeProtocol.Version - 1;

        var result = await IssueAsync(ticket);

        Assert.Equal("protocol_version_mismatch", result.ErrorCode);
    }

    private static async Task<ActiveSimulationBotTicketResult> IssueAsync(JsonObject ticket)
    {
        var options = ActiveSimulationBotOptions.FromConfiguration(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ActiveSimulationBots:AuthorityUrl"] = "http://127.0.0.1:5000",
                    ["ActiveSimulationBots:AuthoritySecret"] = new string('s', 40),
                    ["ActiveSimulationBots:ShardId"] = "local-shard-2"
                })
                .Build());
        using var httpClient = new HttpClient(new StaticResponseHandler(ticket.ToJsonString()))
        {
            BaseAddress = options.AuthorityUrl
        };

        return await new ActiveSimulationBotAuthorityClient(httpClient, options).IssueTicketAsync(
            Guid.NewGuid(),
            1,
            TestContext.Current.CancellationToken);
    }

    private static JsonObject Ticket()
    {
        return new JsonObject
        {
            ["accountId"] = Guid.NewGuid(),
            ["characterId"] = Guid.NewGuid(),
            ["characterName"] = "Active Bot 1",
            ["shardId"] = "local-shard-2",
            ["worldId"] = "development-world-1",
            ["endpoint"] = new JsonObject
            {
                ["workerId"] = "worker-2",
                ["runtimeId"] = "runtime-2",
                ["host"] = "127.0.0.1",
                ["udpPort"] = 27016,
                ["protocolVersion"] = RealtimeProtocol.Version,
                ["simulationRevision"] = "simulation-revision",
                ["collisionRevision"] = "collision-revision"
            },
            ["joinTicket"] = "development-bot.ticket",
            ["expiresAt"] = "2026-10-25T12:00:30Z"
        };
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
