using System.Net;
using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// Characterizes join, reconnect, leave, timeout, orphan and shutdown behavior of the
// realtime service over real UDP. The lingering body that a leave or a lost connection leaves
// behind is pinned in RealtimeLingeringBodyTests.
public sealed class RealtimeSessionLifecycleTests
{
    [Fact]
    public async Task JoinRejectedByGameServiceDisconnectsAfterTheGracePeriod()
    {
        await using var harness = RealtimeServerHarness.Create();
        harness.GameService.OnConsume = (_, _) => Task.FromResult(
            ScriptedGameServiceHandler.Problem(
                HttpStatusCode.Conflict,
                "join_ticket_consumed",
                "The join ticket was already used."));
        await harness.StartAsync();

        var client = harness.Connect("rejected", "used-ticket");
        await harness.PollUntilAsync(
            () => client.JoinRejected is not null && client.IsDisconnected,
            "the join rejection and disconnect");

        var rejection = client.JoinRejected!;
        Assert.Equal("join_ticket_consumed", rejection.Code);
        Assert.Equal("The join ticket was already used.", rejection.Message);
        var reliable = client.ReliablePackets;
        Assert.Equal(RealtimeMessageType.JoinRejected, Assert.Single(reliable).Type);
        var metrics = harness.NetworkMetrics!.Capture();
        Assert.Equal(1, metrics.RejectedJoins);
        Assert.Equal(0, metrics.AcceptedJoins);
        var denial = Assert.Single(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Template == "[SIMULATION] UDP peer {PeerId} was denied shard access with code {Code}: {Message}");

        // The worker logs the denial before it sends JoinRejected and schedules the delayed
        // disconnect, and a late client poll only records the disconnect later, so this
        // bound holds under any machine load.
        Assert.True(
            client.DisconnectedAtUtc!.Value - denial.LoggedAtUtc >= TimeSpan.FromMilliseconds(150),
            "The server must keep the peer for the graceful disconnect delay.");
    }

    [Fact]
    public async Task ReconnectWithTheSameSessionReusesTheEntityAndDetachesTheOldClient()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Reconnect Hero");
        await harness.StartAsync();
        var first = await harness.JoinAsync(character, "first");
        var reconnect = character with
        {
            Ticket = "ticket-reconnect",
            SimulationSessionToken = "reconnected-session-token"
        };
        harness.GameService.AddCharacter(reconnect);

        var second = await harness.JoinAsync(reconnect, "second");
        await harness.PollUntilAsync(
            () => first.ServerDisconnect is not null && first.IsDisconnected,
            "the first client to be detached");

        Assert.Equal("session_reconnected", first.ServerDisconnect!.Code);
        Assert.Equal(
            "This character connected from another client.",
            first.ServerDisconnect.Message);
        Assert.Equal(RealtimeMessageType.ServerDisconnect, first.ReliablePackets[^1].Type);
        Assert.Equal(
            first.JoinAccepted!.ControlledEntityId,
            second.JoinAccepted!.ControlledEntityId);
        Assert.True(second.JoinAccepted.IsReconnect);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
        var session = Assert.Single(harness.SessionStore.ListActiveSessions());
        Assert.Equal("reconnected-session-token", session.SimulationSessionToken);
        Assert.Equal(0, harness.GameService.ReleaseCount);
    }

    [Fact]
    public async Task PeerWithoutJoinIsDisconnectedAfterTheHandshakeTimeout()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            JoinHandshakeTimeout = TimeSpan.FromMilliseconds(500)
        });
        await harness.StartAsync();
        var connectedAt = DateTime.UtcNow;
        var client = harness.Connect("silent");

        await RealtimeProtocolRejectionTests.AssertRejectedAsync(
            harness,
            client,
            "join_timeout",
            expectLastReliable: true);

        Assert.Equal("The join handshake timed out.", client.ServerDisconnect!.Message);
        Assert.True(
            client.ReliablePackets[^1].ReceivedAtUtc - connectedAt
                >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task SessionWithoutAPeerIsReleasedByTheOrphanSweep()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            JoinHandshakeTimeout = TimeSpan.FromMilliseconds(300)
        });
        var session = new ActiveSimulationSession(
            Guid.NewGuid(),
            "orphan-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Orphan Hero",
            RealtimeServerHarness.ShardId,
            RealtimeServerHarness.WorldId,
            RealtimeServerHarness.WorkerId,
            RealtimeServerHarness.RuntimeId,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5),
            new PlayerCarryState(0, 0, 200),
            false);
        Assert.Equal(
            ActiveSimulationSessionRegistration.Joined,
            harness.SessionStore.Register(session));
        harness.CharacterItemStateStore.Register(
            session.CharacterId,
            session.SimulationSessionId,
            CharacterStateTestData.ItemState(session.CarryState),
            out _);
        await harness.StartAsync();

        await harness.PollUntilAsync(
            () => harness.GameService.ReleaseCount == 1
                && harness.SessionStore.ListActiveSessions().Count == 0,
            "the orphan sweep to release the session");

        Assert.False(harness.CharacterItemStateStore.TryGet(
            session.CharacterId,
            session.SimulationSessionId,
            out _));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Template == "[SIMULATION] Releasing simulation session {SimulationSessionId} because no peer has owned it for {Seconds:F0} seconds.");
    }

    [Fact]
    public async Task ShutdownWaitsForAnInFlightJoinAndLogsTheStopLast()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Shutdown Hero");
        var observerCharacter = harness.AddCharacter("Shutdown Observer");
        var gate = new TestGate();
        harness.GameService.OnConsume = async (request, _) =>
        {
            if (request.Ticket == character.Ticket)
            {
                await gate.WaitAsync();
            }

            return harness.GameService.DefaultConsume(request);
        };
        await harness.StartAsync();
        await harness.JoinAsync(observerCharacter, "observer");
        harness.Connect("pending", character.Ticket);
        await harness.PollUntilAsync(() => gate.HasEntered, "the join to start");

        var stop = harness.StopAsync();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);

        gate.Release();
        using var timeout = RealtimeServerHarness.CreateTimeout();
        await stop.WaitAsync(timeout.Token);

        Assert.Equal(0, harness.EntityRegistry.PlayerCount);
        Assert.False(harness.Bindings.TryGetEntityId(0, out _));
        var metrics = harness.NetworkMetrics!.Capture();
        Assert.Equal(0, metrics.ActivePeers);
        Assert.Equal(0, metrics.ActiveEntities);
        Assert.Equal(0, metrics.ActiveRealPlayers);
        // In-flight operations finish before the stores are cleared, so a late join cannot
        // leave carry state behind.
        Assert.False(harness.CharacterItemStateStore.TryGet(
            character.CharacterId,
            character.SimulationSessionId,
            out _));
        var last = harness.Logger.Entries[^1];
        Assert.Equal(LogLevel.Information, last.Level);
        Assert.Equal("[SIMULATION] Realtime transport stopped.", last.Template);
    }

    [Fact]
    public async Task LifecycleLogsUseInformationForPlayersAndDebugForBots()
    {
        await using var harness = RealtimeServerHarness.Create();
        var player = harness.AddCharacter("Logged Player");
        var bot = harness.AddCharacter("Logged Bot", isSyntheticBot: true);
        await harness.StartAsync();
        var playerClient = await harness.JoinAsync(player);
        var botClient = await harness.JoinAsync(bot);

        playerClient.SendLeave(player.SimulationSessionId.ToString("D"));
        botClient.SendLeave(bot.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(
            () => playerClient.Has(RealtimeMessageType.LeaveAccepted)
                && botClient.Has(RealtimeMessageType.LeaveAccepted),
            "both leaves");

        var entries = harness.Logger.Entries;
        Assert.Contains(
            entries,
            entry => entry.Level == LogLevel.Information
                && entry.Template == "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) connected to shard {ShardId} for world {WorldId}. Simulation session {SimulationSessionId}, network entity {EntityId}, UDP peer {PeerId}.");
        Assert.Contains(
            entries,
            entry => entry.Level == LogLevel.Debug
                && entry.Template == "[SIMULATION] Synthetic bot {CharacterName} joined shard {ShardId}. Simulation session {SimulationSessionId}, network entity {EntityId}, UDP peer {PeerId}.");
        Assert.Contains(
            entries,
            entry => entry.Level == LogLevel.Information
                && entry.Template == "[SIMULATION] Account {AccountId} with character {CharacterName} ({CharacterId}) left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.");
        Assert.Contains(
            entries,
            entry => entry.Level == LogLevel.Debug
                && entry.Template == "[SIMULATION] Synthetic bot {CharacterName} left shard {ShardId}. Simulation session {SimulationSessionId}, UDP peer {PeerId}.");
        Assert.Contains(
            entries,
            entry => entry.Level == LogLevel.Information
                && entry.Template == "[SIMULATION] Realtime transport listening on UDP port {UdpPort} with protocol version {ProtocolVersion}, {TickRate} simulation ticks per second, and {SnapshotRate} snapshots per second.");
        var metrics = harness.NetworkMetrics!.Capture();
        Assert.Equal(2, metrics.AcceptedJoins);
    }
}
