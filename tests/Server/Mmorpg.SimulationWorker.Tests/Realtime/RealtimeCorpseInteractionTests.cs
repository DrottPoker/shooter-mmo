using System.Net;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.GameServiceApi;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Characterizes corpse presence, view distribution and corpse authority results over UDP.
// The player spawns at (0, 0, -1), so a corpse at (1, 0, -1) is within interaction range.
public sealed class RealtimeCorpseInteractionTests
{
    [Fact]
    public async Task PresenceListsOnlyCorpsesWithinTheDiscoveryRadius()
    {
        await using var harness = RealtimeServerHarness.Create();
        var nearCorpseId = Guid.NewGuid();
        var farCorpseId = Guid.NewGuid();
        harness.SeedDurableCorpse(nearCorpseId, 1d, 0d, -1d);
        harness.SeedDurableCorpse(farCorpseId, 60d, 0d, 60d);
        var character = harness.AddCharacter("Presence Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        await harness.PollUntilAsync(
            () => client.CorpsePresence.Count > 0,
            "the corpse presence");

        var chunk = client.CorpsePresence[0];
        var corpse = Assert.Single(chunk.Corpses);
        Assert.Equal(nearCorpseId, corpse.CorpseId);
        Assert.Equal(1f, corpse.PositionX);
        Assert.Equal(RealtimeProtocol.ControlChannel, client.Packets.First(packet =>
            packet.Type == RealtimeMessageType.CorpsePresenceSnapshotChunk).Channel);
    }

    [Fact]
    public async Task OpenSendsTheResultBeforeTheSnapshotAndDeltasReachExistingViewers()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        var opens = 0;
        harness.GameService.OnCorpseOpen = (_, openedCorpseId, _, _) =>
        {
            var revision = Interlocked.Increment(ref opens);
            return Task.FromResult(ScriptedGameServiceHandler.Json(
                RealtimeCorpseTestData.Snapshot(
                    openedCorpseId,
                    revision,
                    itemInstanceId: revision == 1 ? itemId : null)));
        };
        var firstCharacter = harness.AddCharacter("First Viewer");
        var secondCharacter = harness.AddCharacter("Second Viewer");
        await harness.StartAsync();
        var first = await harness.JoinAsync(firstCharacter);
        var second = await harness.JoinAsync(secondCharacter);

        var firstOpen = RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId);
        first.SendCorpseIntent(firstOpen);
        await harness.PollUntilAsync(
            () => first.CorpseResults.Count == 1 && HasCompleteSnapshot(first),
            "the first open and its snapshot");

        var openResult = Assert.Single(first.CorpseResults);
        Assert.True(openResult.Succeeded);
        Assert.Equal(firstOpen.OperationId, openResult.OperationId);
        Assert.True(
            first.IndexOfFirst(RealtimeMessageType.CorpseInteractionResult)
                < first.IndexOfFirst(RealtimeMessageType.CorpseViewStateChunk));
        Assert.All(first.CorpseViewChunks, chunk =>
        {
            Assert.Equal(RealtimeCorpseViewUpdateKind.Snapshot, chunk.UpdateKind);
            Assert.Equal(1, chunk.Revision);
        });
        var firstChunkCount = first.CorpseViewChunks.Count;

        second.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            corpseId));
        await harness.PollUntilAsync(
            () => second.CorpseResults.Count == 1
                && second.CorpseViewChunks.Count > 0
                && first.CorpseViewChunks.Count > firstChunkCount,
            "the second open and the delta");

        Assert.All(second.CorpseViewChunks, chunk =>
        {
            Assert.Equal(RealtimeCorpseViewUpdateKind.Snapshot, chunk.UpdateKind);
            Assert.Equal(2, chunk.Revision);
        });
        Assert.All(first.CorpseViewChunks.Skip(firstChunkCount), chunk =>
        {
            Assert.Equal(RealtimeCorpseViewUpdateKind.Delta, chunk.UpdateKind);
            Assert.Equal(1, chunk.BaseRevision);
            Assert.Equal(2, chunk.Revision);
        });
        Assert.Equal(2, harness.CorpseViewers.GetViewers(corpseId).Count);
    }

    [Fact]
    public async Task CloseSendsTheResultThenTheViewClosure()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        harness.GameService.OnCorpseOpen = (_, openedCorpseId, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Json(RealtimeCorpseTestData.Snapshot(openedCorpseId, 1)));
        var character = harness.AddCharacter("Closing Viewer");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        client.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            corpseId));
        await harness.PollUntilAsync(() => client.CorpseResults.Count == 1, "the open");

        var close = RealtimeCorpseInteractionIntent.CreateClose(Guid.NewGuid(), corpseId, 1);
        client.SendCorpseIntent(close);
        await harness.PollUntilAsync(
            () => client.CorpseViewClosures.Count == 1,
            "the close");

        var closeResult = client.CorpseResults[^1];
        Assert.Equal(close.OperationId, closeResult.OperationId);
        Assert.True(closeResult.Succeeded);
        var closed = Assert.Single(client.CorpseViewClosures);
        Assert.Equal("corpse_view_closed", closed.Code);
        Assert.Equal("The corpse view was closed.", closed.Message);
        var reliable = client.ReliablePackets;
        var resultIndex = reliable.Last(packet =>
            packet.Type == RealtimeMessageType.CorpseInteractionResult).Index;
        var closedIndex = reliable.Single(packet =>
            packet.Type == RealtimeMessageType.CorpseViewClosed).Index;
        Assert.True(resultIndex < closedIndex);
        Assert.Empty(harness.CorpseViewers.GetViewers(corpseId));
        Assert.Equal(1, harness.GameService.CorpseOpenCount);
    }

    [Fact]
    public async Task NotFoundOnRefreshClosesTheViewForEveryViewer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        var corpseGone = false;
        harness.GameService.OnCorpseOpen = (_, openedCorpseId, _, _) => Task.FromResult(
            Volatile.Read(ref corpseGone)
                ? ScriptedGameServiceHandler.Problem(
                    HttpStatusCode.NotFound,
                    "corpse_not_found",
                    "The corpse is gone.")
                : ScriptedGameServiceHandler.Json(
                    RealtimeCorpseTestData.Snapshot(openedCorpseId, 1)));
        var firstCharacter = harness.AddCharacter("Refreshing Viewer");
        var secondCharacter = harness.AddCharacter("Watching Viewer");
        await harness.StartAsync();
        var first = await harness.JoinAsync(firstCharacter);
        var second = await harness.JoinAsync(secondCharacter);
        first.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId));
        second.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId));
        await harness.PollUntilAsync(
            () => first.CorpseResults.Count == 1 && second.CorpseResults.Count == 1,
            "both opens");

        Volatile.Write(ref corpseGone, true);
        var refresh = RealtimeCorpseInteractionIntent.CreateRefresh(Guid.NewGuid(), corpseId, 1);
        first.SendCorpseIntent(refresh);
        await harness.PollUntilAsync(
            () => first.CorpseViewClosures.Count == 1 && second.CorpseViewClosures.Count == 1,
            "the closure for every viewer");

        var refreshResult = first.CorpseResults[^1];
        Assert.Equal(refresh.OperationId, refreshResult.OperationId);
        Assert.False(refreshResult.Succeeded);
        Assert.Equal("corpse_not_found", refreshResult.Error.Code);
        Assert.Equal("corpse_not_found", first.CorpseViewClosures[0].Code);
        Assert.Equal("The corpse is gone.", second.CorpseViewClosures[0].Message);
        Assert.False(harness.CorpseStore.TryGetActive(corpseId, out _));
        Assert.Empty(harness.CorpseViewers.GetViewers(corpseId));
    }

    [Fact]
    public async Task ExpiredCorpseClosesTheViewForEveryViewer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        harness.GameService.OnCorpseOpen = (_, openedCorpseId, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Json(RealtimeCorpseTestData.Snapshot(openedCorpseId, 1)));
        var character = harness.AddCharacter("Expiry Viewer");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        client.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId));
        // The server sends the open result before it stores the opened snapshot, and the
        // view state after. Removing the corpse before the view state arrives races with
        // that store update, which would bring the corpse back.
        await harness.PollUntilAsync(
            () => client.CorpseResults.Count == 1 && client.CorpseViewChunks.Count > 0,
            "the open");

        Assert.True(harness.CorpseStore.Remove(corpseId));
        await harness.PollUntilAsync(
            () => client.CorpseViewClosures.Count == 1,
            "the expiry closure");

        var closed = Assert.Single(client.CorpseViewClosures);
        Assert.Equal(corpseId, closed.CorpseId);
        Assert.Equal("corpse_expired", closed.Code);
        Assert.Equal("The corpse expired and is no longer available.", closed.Message);
        Assert.Empty(harness.CorpseViewers.GetViewers(corpseId));
    }

    [Theory]
    [InlineData("out_of_range", "corpse_out_of_range", "Move closer to the corpse before interacting with it.")]
    [InlineData("unknown", "corpse_not_found", "The corpse is no longer active on this Shard.")]
    [InlineData("not_open", "corpse_view_not_open", "Open the corpse before refreshing or looting it.")]
    public async Task LocalCorpseRejectionsDoNotReachGameService(
        string scenario,
        string expectedCode,
        string expectedMessage)
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        if (scenario == "out_of_range")
        {
            harness.SeedDurableCorpse(corpseId, 10d, 0d, 10d);
        }
        else if (scenario == "not_open")
        {
            harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        }

        var character = harness.AddCharacter("Rejected Viewer");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var intent = scenario == "not_open"
            ? RealtimeCorpseInteractionIntent.CreateRefresh(Guid.NewGuid(), corpseId, 1)
            : RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId);

        client.SendCorpseIntent(intent);
        await harness.PollUntilAsync(() => client.CorpseResults.Count == 1, "the rejection");

        var result = Assert.Single(client.CorpseResults);
        Assert.Equal(intent.OperationId, result.OperationId);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedMessage, result.Error.Message);
        Assert.Equal(0, harness.GameService.CorpseOpenCount);
        Assert.Empty(client.CorpseViewChunks);
        Assert.Null(client.ServerDisconnect);
    }

    [Fact]
    public async Task CorpseQueueFullUsesTheSharedAuthorityQueueMessage()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        var gate = new TestGate();
        harness.GameService.OnCorpseOpen = async (_, openedCorpseId, _, _) =>
        {
            await gate.WaitAsync();
            return ScriptedGameServiceHandler.Json(
                RealtimeCorpseTestData.Snapshot(openedCorpseId, 1));
        };
        var character = harness.AddCharacter("Queued Viewer");
        await harness.StartAsync();
        try
        {
            var client = await harness.JoinAsync(character);
            var intents = Enumerable.Range(0, 10)
                .Select(_ => RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId))
                .ToArray();
            foreach (var intent in intents)
            {
                client.SendCorpseIntent(intent);
            }

            await harness.PollUntilAsync(() => client.CorpseResults.Count == 1, "the rejection");
            var rejection = Assert.Single(client.CorpseResults);
            Assert.Equal(intents[9].OperationId, rejection.OperationId);
            Assert.Equal("item_operation_queue_full", rejection.Error.Code);
            Assert.Equal(
                "Too many item or corpse operations are waiting for authority.",
                rejection.Error.Message);

            gate.Release();
            await harness.PollUntilAsync(
                () => client.CorpseResults.Count == 10,
                "the queued corpse results");
            Assert.Equal(
                intents.Take(9).Select(intent => intent.OperationId),
                client.CorpseResults.Skip(1).Select(result => result.OperationId));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task LootCommitsCarryStateAndSendsTheDeltaToEveryViewer()
    {
        await using var harness = RealtimeServerHarness.Create();
        var corpseId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        harness.SeedDurableCorpse(corpseId, 1d, 0d, -1d);
        harness.GameService.OnCorpseOpen = (_, openedCorpseId, _, _) => Task.FromResult(
            ScriptedGameServiceHandler.Json(RealtimeCorpseTestData.Snapshot(
                openedCorpseId,
                1,
                itemInstanceId: itemId)));
        harness.GameService.OnCorpseMutation = (_, mutatedCorpseId, request, _) =>
            Task.FromResult(ScriptedGameServiceHandler.Json(new CorpseMutationResponse(
                new SimulationItemTransactionResponse(
                    request.OperationId,
                    "loot_corpse_item",
                    true,
                    null,
                    [new SimulationItemCharacterRevision(request.CharacterId, 1, 1, 200)],
                    [],
                    [],
                    [],
                    null),
                RealtimeCorpseTestData.Snapshot(mutatedCorpseId, 2),
                CharacterStateTestData.Create(request.CharacterId, 1, 1))));
        var looterCharacter = harness.AddCharacter("Looter");
        var watcherCharacter = harness.AddCharacter("Loot Watcher");
        await harness.StartAsync();
        var looter = await harness.JoinAsync(looterCharacter);
        var watcher = await harness.JoinAsync(watcherCharacter);
        looter.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId));
        watcher.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId));
        // The open result arrives before its snapshot chunks, so wait for the last chunk
        // of each snapshot; otherwise a late snapshot chunk counts as part of the delta.
        await harness.PollUntilAsync(
            () => looter.CorpseResults.Count == 1
                && watcher.CorpseResults.Count == 1
                && HasCompleteSnapshot(looter)
                && HasCompleteSnapshot(watcher),
            "both opens and their snapshots");
        var looterChunks = looter.CorpseViewChunks.Count;
        var watcherChunks = watcher.CorpseViewChunks.Count;

        var loot = RealtimeCorpseInteractionIntent.CreateLootItem(
            Guid.NewGuid(),
            corpseId,
            1,
            itemId,
            1,
            Guid.NewGuid(),
            0,
            0,
            Guid.Empty,
            0);
        looter.SendCorpseIntent(loot);
        await harness.PollUntilAsync(
            () => looter.CorpseResults.Count == 2
                && looter.CorpseViewChunks.Count > looterChunks
                && watcher.CorpseViewChunks.Count > watcherChunks
                && looter.Has(RealtimeMessageType.CarryStateChanged),
            "the loot result and delta");

        var lootResult = looter.CorpseResults[^1];
        Assert.True(lootResult.Succeeded);
        Assert.True(lootResult.RequiresInventoryRefresh);
        Assert.Equal(1, lootResult.CarryState.ItemStateRevision);
        Assert.All(looter.CorpseViewChunks.Skip(looterChunks), chunk =>
            Assert.Equal(RealtimeCorpseViewUpdateKind.Delta, chunk.UpdateKind));
        Assert.All(watcher.CorpseViewChunks.Skip(watcherChunks), chunk =>
            Assert.Equal(RealtimeCorpseViewUpdateKind.Delta, chunk.UpdateKind));
        var reliable = looter.ReliablePackets;
        var carryIndex = reliable.First(packet =>
            packet.Type == RealtimeMessageType.CarryStateChanged).Index;
        var resultIndex = reliable.Last(packet =>
            packet.Type == RealtimeMessageType.CorpseInteractionResult).Index;
        var deltaIndex = reliable.Last(packet =>
            packet.Type == RealtimeMessageType.CorpseViewStateChunk).Index;
        Assert.True(carryIndex < resultIndex);
        Assert.True(resultIndex < deltaIndex);
        Assert.Equal(1, harness.GameService.CorpseMutationCount);
    }

    [Fact]
    public async Task LiveMobCorpseOpensFromTheLocalStore()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableLiveMobCorpses = true
        });
        var createdAt = DateTime.UtcNow;
        var corpse = harness.LiveMobCorpseStore!.CreateOrGet(new LiveMobCorpseState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "mob.feral_wolf",
            "Feral Wolf",
            RealtimeServerHarness.ShardId,
            1d,
            0d,
            -1d,
            MobCorpseLifecycleService.DefaultPresentationKey,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            createdAt,
            createdAt.AddMinutes(2),
            [new LiveMobCorpseLootEntry(Guid.NewGuid(), Guid.NewGuid(), "fixture.ore", 3, 0)]));
        var character = harness.AddCharacter("Live Looter");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        await harness.PollUntilAsync(
            () => client.CorpsePresence.Any(chunk =>
                chunk.Corpses.Any(value => value.CorpseId == corpse.CorpseId)),
            "the live corpse presence");

        client.SendCorpseIntent(RealtimeCorpseInteractionIntent.CreateOpen(
            Guid.NewGuid(),
            corpse.CorpseId));
        await harness.PollUntilAsync(
            () => client.CorpseResults.Count == 1 && client.CorpseViewChunks.Count > 0,
            "the live corpse open");

        Assert.True(Assert.Single(client.CorpseResults).Succeeded);
        Assert.Equal(0, harness.GameService.CorpseOpenCount);
        Assert.False(harness.CorpseStore.TryGetActive(corpse.CorpseId, out _));
        var metrics = harness.CorpseRuntimeMetrics!.Capture();
        Assert.Equal(1, metrics.LiveCorpses);
    }

    // Chunks of one snapshot arrive in order on the reliable ordered channel, so the
    // snapshot is complete once its last chunk has arrived.
    private static bool HasCompleteSnapshot(RealtimeRecordingClient client)
    {
        return client.CorpseViewChunks.Any(chunk =>
            chunk.UpdateKind == RealtimeCorpseViewUpdateKind.Snapshot
            && chunk.ChunkIndex == chunk.ChunkCount - 1);
    }
}
