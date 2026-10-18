using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Characterizes the per-peer authority queue and the item completion paths over UDP.
public sealed class RealtimeItemOperationTests
{
    [Fact]
    public async Task TenthIntentIsRejectedWhileOneRunsAndEightWait()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Queue Hero");
        var gate = new TestGate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return ScriptedGameServiceHandler.CommittedItemOperation(request, 1, 0, 200);
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            var intents = Enumerable.Range(0, 10)
                .Select(_ => RealtimeProtocolRejectionTests.CreateItemIntent())
                .ToArray();
            foreach (var intent in intents)
            {
                client.SendItemIntent(intent);
            }

            await harness.PollUntilAsync(
                () => client.ItemResults.Count == 1,
                "the queue-full rejection");
            var rejection = Assert.Single(client.ItemResults);
            Assert.Equal(intents[9].OperationId, rejection.OperationId);
            Assert.False(rejection.Succeeded);
            Assert.Equal("item_operation_queue_full", rejection.Error.Code);
            Assert.Equal(
                "Too many item operations are waiting for authority.",
                rejection.Error.Message);
            Assert.False(rejection.RequiresInventoryRefresh);

            gate.Release();
            await harness.PollUntilAsync(
                () => client.ItemResults.Count == 10,
                "the queued item results");

            var completed = client.ItemResults.Skip(1).ToArray();
            Assert.Equal(
                intents.Take(9).Select(intent => intent.OperationId),
                completed.Select(result => result.OperationId));
            Assert.All(completed, result => Assert.True(result.Succeeded));
            Assert.Equal(9, harness.GameService.ItemOperationCount);
            Assert.Null(client.ServerDisconnect);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task DisconnectingGameServiceErrorInvalidatesTheSession()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Invalidated Hero");
        harness.GameService.OnItemOperation = (_, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(
                HttpStatusCode.Conflict,
                "simulation_session_invalid",
                "The simulation session is no longer valid."));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var intent = RealtimeProtocolRejectionTests.CreateItemIntent();

        client.SendItemIntent(intent);
        await harness.PollUntilAsync(
            () => client.ServerDisconnect is not null && client.IsDisconnected,
            "the authority failure");

        var result = Assert.Single(client.ItemResults);
        Assert.Equal(intent.OperationId, result.OperationId);
        Assert.False(result.Succeeded);
        Assert.True(result.RequiresInventoryRefresh);
        Assert.Equal("simulation_session_invalid", result.Error.Code);
        Assert.Equal("simulation_session_invalid", client.ServerDisconnect!.Code);
        Assert.Equal("The simulation session is no longer valid.", client.ServerDisconnect.Message);
        var reliable = client.ReliablePackets;
        Assert.Equal(RealtimeMessageType.ServerDisconnect, reliable[^1].Type);
        Assert.Equal(RealtimeMessageType.ItemOperationResult, reliable[^2].Type);
        Assert.Empty(harness.SessionStore.ListActiveSessions());
        Assert.Equal(0, harness.EntityRegistry.PlayerCount);
        Assert.False(harness.CharacterItemStateStore.TryGet(
            character.CharacterId,
            character.SimulationSessionId,
            out _));
    }

    [Fact]
    public async Task ConflictingCommittedCarryDivergesTheSession()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Diverged Hero");
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedItemOperation(request, 0, 50, 200));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
        await harness.PollUntilAsync(
            () => client.ServerDisconnect is not null && client.IsDisconnected,
            "the divergence disconnect");

        Assert.Empty(client.ItemResults);
        Assert.Equal("item_state_diverged", client.ServerDisconnect!.Code);
        Assert.Equal(
            "Committed item and carry revisions diverged from the active simulation state.",
            client.ServerDisconnect.Message);
        Assert.Equal(RealtimeMessageType.ServerDisconnect, client.ReliablePackets[^1].Type);
        Assert.Empty(harness.SessionStore.ListActiveSessions());
    }

    [Fact]
    public async Task InvalidCharacterRevisionSetIsAnInvalidGameServiceResponse()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Invalid Response Hero");
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.Json(new SimulationItemTransactionResponse(
                request.OperationId,
                request.OperationKind,
                true,
                null,
                [
                    new SimulationItemCharacterRevision(request.CharacterId, 1, 0, 200),
                    new SimulationItemCharacterRevision(request.CharacterId, 2, 0, 200)
                ],
                [],
                [],
                [],
                null)));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
        await harness.PollUntilAsync(
            () => client.ServerDisconnect is not null && client.IsDisconnected,
            "the invalid response disconnect");

        // GameServiceClient already rejects more than one character revision, so the
        // completion handler never sees such a transaction from the network.
        Assert.Equal("invalid_auth_response", Assert.Single(client.ItemResults).Error.Code);
        Assert.Equal("invalid_auth_response", client.ServerDisconnect!.Code);
    }

    [Fact]
    public async Task StaleCommittedCarryRequestsAnInventoryRefresh()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Stale Hero") with { ItemStateRevision = 5 };
        harness.GameService.AddCharacter(character);
        harness.GameService.OnItemOperation = (_, request, _) => Task.FromResult(
            ScriptedGameServiceHandler.CommittedItemOperation(request, 3, 0, 200));
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
        await harness.PollUntilAsync(() => client.ItemResults.Count == 1, "the stale result");

        var result = Assert.Single(client.ItemResults);
        Assert.True(result.Succeeded);
        Assert.True(result.RequiresInventoryRefresh);
        Assert.Equal(5, result.CarryState.ItemStateRevision);
        Assert.Null(client.ServerDisconnect);
    }

    [Fact]
    public async Task CarryStateLostDuringTheOperationDisconnectsThePeer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Lost Carry Hero");
        var gate = new TestGate();
        harness.GameService.OnItemOperation = async (_, request, _) =>
        {
            await gate.WaitAsync();
            return ScriptedGameServiceHandler.CommittedItemOperation(request, 1, 0, 200);
        };
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            client.SendItemIntent(RealtimeProtocolRejectionTests.CreateItemIntent());
            await harness.PollUntilAsync(() => gate.HasEntered, "the item operation to start");

            Assert.True(harness.CharacterItemStateStore.Remove(
                character.CharacterId,
                character.SimulationSessionId));
            gate.Release();
            await harness.PollUntilAsync(
                () => client.ServerDisconnect is not null && client.IsDisconnected,
                "the carry-state disconnect");

            Assert.Empty(client.ItemResults);
            Assert.Equal("carry_state_missing", client.ServerDisconnect!.Code);
            Assert.Equal(
                "Authoritative carry state disappeared during the item operation.",
                client.ServerDisconnect.Message);
        }
        finally
        {
            gate.Release();
        }
    }
}
