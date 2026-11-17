using System.Buffers.Binary;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The sealed transport end to end over real LiteNetLib on loopback, with a relay on the path
// between the client and the worker: the join carries no secret, every datagram after it is
// sealed in both directions, what the path injects changes nothing and is counted, and a
// reconnect brings new keys.
public sealed class RealtimeSealedTransportTests
{
    [Fact]
    public async Task AJoinedSessionIsSealedBothWaysAndWhatThePathInjectsChangesNothing()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Sealed Walker");
        await harness.StartAsync();
        using var relay = new LossyUdpRelay(harness.Port);
        var client = await harness.JoinAsync(character, viaPort: relay.Port);

        // The join carried the ticket id, never the transport key or a key derived from it.
        Assert.True(client.Security.IsSealing);
        using var keys = RealtimeTestTickets.KeysOf(character.Ticket);
        var secrets = new[]
        {
            RealtimeTestTickets.TransportKeyOf(character.Ticket),
            keys.ProofKey,
            keys.ClientToWorkerKey,
            keys.WorkerToClientKey
        };
        Assert.DoesNotContain(relay.ToWorker, datagram => secrets.Any(secret => Contains(datagram, secret)));
        Assert.Contains(relay.ToWorker, datagram => datagram[0] == RealtimeDatagram.ClearMarker
            && Contains(datagram, RealtimeTestTickets.IdOf(character.Ticket).ToByteArray()));

        client.SendMoveTo(1, 8f, 2.25f);
        await harness.PollUntilAsync(
            () => client.OwnerSnapshots.Any(snapshot => snapshot.LastProcessedInputSequence >= 1),
            "the worker to take the first input");

        // From the worker's first sealed datagram on, both directions are sealed only.
        AssertSealedFromTheFirstSealed(relay.ToClient, "to the client");
        AssertSealedFromTheFirstSealed(relay.ToWorker, "to the worker");
        var sealedFromWorker = relay.ToClient.Last(datagram => datagram[0] == RealtimeDatagram.SealedMarker);
        var sealedFromClient = relay.ToWorker.Last(datagram => datagram[0] == RealtimeDatagram.SealedMarker);
        var workerDroppedBefore = harness.NetworkMetrics!.Capture().DroppedDatagrams;
        var clientDroppedBefore = client.Security.Counters.Dropped;

        // A third socket on the path injects clear packets, garbage, and replays both ways: an
        // owner channel packet and a disconnect in clear, a sealed datagram that a key never
        // made, a datagram of no kind, and copies of datagrams the other side accepted.
        var injectedToWorker = new[]
        {
            Clear(Channeled(14, 30)),
            Clear([8, 0, 0, 0, 0, 0, 0, 0, 0]),
            Garbage(seed: 1),
            [7, 1, 2, 3, 4],
            sealedFromClient
        };
        var injectedToClient = new[]
        {
            Clear(Channeled(2, 30)),
            Clear([8, 0, 0, 0, 0, 0, 0, 0, 0]),
            Garbage(seed: 2),
            [9, 9, 9],
            sealedFromWorker
        };
        foreach (var datagram in injectedToWorker)
        {
            relay.InjectToWorker(datagram);
        }

        foreach (var datagram in injectedToClient)
        {
            relay.InjectToClient(datagram);
        }

        await harness.PollUntilAsync(
            () => harness.NetworkMetrics.Capture().DroppedDatagrams - workerDroppedBefore >= injectedToWorker.Length
                && client.Security.Counters.Dropped - clientDroppedBefore >= injectedToClient.Length,
            "both sides to drop what the path injected");

        // The session goes on as before: the next input arrives and is answered.
        client.SendMoveTo(2, -6f, 2.25f);
        await harness.PollUntilAsync(
            () => client.OwnerSnapshots.Any(snapshot => snapshot.LastProcessedInputSequence >= 2),
            "the worker to take the second input");
        Assert.False(client.IsDisconnected);
        Assert.Null(client.ServerDisconnect);
        Assert.Equal(injectedToWorker.Length, harness.NetworkMetrics.Capture().DroppedDatagrams - workerDroppedBefore);
        Assert.Equal(injectedToClient.Length, client.Security.Counters.Dropped - clientDroppedBefore);
        Assert.Equal(2, client.Security.Counters.DroppedClearAfterSealing);
        Assert.Equal(1, client.Security.Counters.DroppedReplayed);
    }

    // A disconnect takes the peer's keys with it; the reconnect's new ticket brings new keys, so
    // what the first session sealed opens nothing of the second, even from the same endpoint.
    [Fact]
    public async Task AReconnectUsesANewTicketAndNewKeys()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Returning Sealer");
        await harness.StartAsync();
        using var relay = new LossyUdpRelay(harness.Port);
        var first = await harness.JoinAsync(character, "first", viaPort: relay.Port);
        first.SendMoveTo(1, 4f, 2.25f);
        await harness.PollUntilAsync(
            () => first.OwnerSnapshots.Any(snapshot => snapshot.LastProcessedInputSequence >= 1),
            "the first session to move");
        var fromFirstSession = relay.ToWorker.Last(datagram => datagram[0] == RealtimeDatagram.SealedMarker);
        first.SendLeave(first.JoinAccepted!.SimulationSessionId);
        await harness.PollUntilAsync(() => first.IsDisconnected, "the first session to end");

        var reconnect = character with { Ticket = "ticket-returning-sealer", SimulationSessionToken = "returned-sealer-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "second", viaPort: relay.Port);
        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.True(second.Security.IsSealing);

        var droppedBefore = harness.NetworkMetrics!.Capture().DroppedDatagrams;
        relay.InjectToWorker(fromFirstSession);
        await harness.PollUntilAsync(
            () => harness.NetworkMetrics.Capture().DroppedDatagrams > droppedBefore,
            "the worker to drop the first session's datagram");
        second.SendMoveTo(1, -4f, 2.25f);
        await harness.PollUntilAsync(
            () => second.OwnerSnapshots.Any(snapshot => snapshot.LastProcessedInputSequence >= 1),
            "the second session to move");
        Assert.False(second.IsDisconnected);
    }

    // LiteNetLib sends a disconnect again every 300 ms until the client confirms it. The peer
    // keeps its keys until then, so a client that lost the first one gets a sealed one again
    // instead of a clear one that it would drop.
    [Fact]
    public async Task ADisconnectThatTheLineLostArrivesSealedAgain()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Patient Leaver");
        await harness.StartAsync();
        using var relay = new LossyUdpRelay(harness.Port);
        var client = await harness.JoinAsync(character, viaPort: relay.Port);
        relay.LoseFirstDisconnectToClient(character.Ticket);

        client.SendLeave(client.JoinAccepted!.SimulationSessionId);
        await harness.PollUntilAsync(() => client.IsDisconnected, "the disconnect after the lost one");

        Assert.Equal(1, relay.LostToClient);
        Assert.Equal(LiteNetLib.DisconnectReason.RemoteConnectionClose, client.DisconnectReason);
        Assert.True(RealtimeProtocol.TryDecodeLeaveAccepted(client.DisconnectData!, out _, out var error), error);
        Assert.Equal(0, client.Security.Counters.DroppedClearAfterSealing);
    }

    private static void AssertSealedFromTheFirstSealed(IReadOnlyList<byte[]> datagrams, string direction)
    {
        var first = datagrams.ToList().FindIndex(datagram => datagram[0] == RealtimeDatagram.SealedMarker);
        Assert.True(first >= 0, $"No sealed datagram went {direction}.");
        var clear = datagrams.Skip(first).Count(datagram => datagram[0] != RealtimeDatagram.SealedMarker);
        Assert.True(clear == 0, $"{clear} clear datagrams went {direction} after the first sealed one.");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private static byte[] Clear(byte[] packet)
    {
        return [RealtimeDatagram.ClearMarker, .. packet];
    }

    // A LiteNetLib 2.1 channeled packet: the property, a sequence, and the channel id.
    private static byte[] Channeled(byte channelId, int payloadBytes)
    {
        return [1, 0, 0, channelId, .. Enumerable.Repeat((byte)0x5a, payloadBytes)];
    }

    // A datagram in the sealed format with a fresh counter that no key made.
    private static byte[] Garbage(int seed)
    {
        var datagram = new byte[80];
        new Random(seed).NextBytes(datagram);
        datagram[0] = RealtimeDatagram.SealedMarker;
        BinaryPrimitives.WriteUInt64LittleEndian(datagram.AsSpan(1), 1_000_000UL + (ulong)seed);
        return datagram;
    }
}
