using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Mmorpg.Testing;
using Mmorpg.Tools.NetworkImpairmentProxy;
using Mmorpg.Tools.StackStressGenerator;

namespace Mmorpg.Tools.Tests;

// The network impairment proxy: its profiles, the fate it decides for each packet, the scheduler
// that sends a packet at its due time, and the proxy on real loopback sockets.
public sealed class NetworkImpairmentProxyTests
{
    private const long Frequency = 1_000_000;

    private static string ProfileFolder => Path.Combine(RepositoryPaths.Root, "tools", "load", NetworkProfile.FolderName);

    public static TheoryData<string> Profiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(ProfileFolder, "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    // The four decided profiles, as round trips split evenly over both directions.
    [Theory]
    [InlineData("good", 30, 5, 0)]
    [InlineData("normal", 80, 15, 1)]
    [InlineData("poor", 200, 40, 5)]
    [InlineData("terrible", 400, 100, 10)]
    public void TheShippedProfilesAreTheFourDecidedOnes(string name, double roundTripMs, double jitterMs, double lossPercent)
    {
        var profile = LoadShipped(name);

        Assert.Equal(roundTripMs, profile.RoundTripMs);
        Assert.Equal(jitterMs, profile.RoundTripJitterMs);
        Assert.Equal(profile.ClientToServer, profile.ServerToClient);
        Assert.Equal(lossPercent, profile.ClientToServer.LossPercent);
        Assert.Equal(name is "poor" or "terrible", profile.ClientToServer.BurstLoss is not null);
        Assert.Equal(name == "terrible", profile.HasOutages);
        if (profile.ClientToServer.Outages is { } outages)
        {
            Assert.Equal(3, outages.DurationSeconds);
            Assert.Equal(60, outages.EverySeconds);
        }
    }

    // The input acknowledgement allowance is the round trip plus the jitter, every lossy profile
    // takes the owner gaps out, and a profile with outages also the remote update rate.
    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryShippedProfileLoadsForTheLoadRunnerWithTheDecidedVerdict(string name)
    {
        var profile = StressNetworkProfiles.Load(Path.Combine(ProfileFolder, name + ".json"));

        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
        var allowance = Assert.Single(profile.Verdict.Allowances);
        Assert.Equal("input_acknowledgement_p99_ms", allowance.Rule);
        Assert.Equal(profile.RoundTripMs + profile.RoundTripJitterMs, allowance.Add);
        var notApplicable = profile.Verdict.NotApplicable.Select(entry => entry.Rule).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(profile.ClientToServer.LossPercent > 0 || profile.ServerToClient.LossPercent > 0, notApplicable.Contains("owner_snapshot_gaps"));
        Assert.Equal(profile.HasOutages, notApplicable.Contains("remote_update_rate_p5_hz"));
    }

    [Theory]
    [InlineData("\"color\": 1,", "unknown property 'color'")]
    [InlineData("\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 6, \"lossPercent\": 0, \"duplicatePercent\": 0 },", "above its delay")]
    [InlineData("\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 1, \"lossPercent\": 100, \"duplicatePercent\": 0 },", "'lossPercent'")]
    [InlineData("\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 1, \"lossPercent\": 0, \"duplicatePercent\": 0, \"burstLoss\": { \"sharePercent\": 20, \"averageLengthPackets\": 4 } },", "needs a loss above 0")]
    [InlineData("\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 1, \"lossPercent\": 0, \"duplicatePercent\": 0, \"outages\": { \"firstAfterSeconds\": 0, \"everySeconds\": 3, \"durationSeconds\": 3 } },", "longer than the duration")]
    [InlineData("\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 1, \"lossPercent\": 0 },", "needs the number 'duplicatePercent'")]
    public void AProfileIsValidatedStrictly(string replacement, string expected)
    {
        var json = Profile(replacement);

        var exception = Assert.Throws<NetworkImpairmentException>(() => NetworkProfile.Parse("bad", json, "bad.json"));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerdictNamesEachRuleOnceAndTheLoadRunnerChecksTheRules()
    {
        var twice = Profile(verdict: "\"allowances\": [ { \"rule\": \"deadlocks\", \"add\": 1, \"reason\": \"r\" } ], \"notApplicable\": [ { \"rule\": \"deadlocks\", \"reason\": \"r\" } ]");
        Assert.Contains(
            "more than once",
            Assert.Throws<NetworkImpairmentException>(() => NetworkProfile.Parse("bad", twice, "bad.json")).Message,
            StringComparison.Ordinal);

        foreach (var (verdict, expected) in new[]
        {
            ("\"allowances\": [], \"notApplicable\": [ { \"rule\": \"no_such_rule\", \"reason\": \"r\" } ]", "does not know"),
            ("\"allowances\": [], \"notApplicable\": [ { \"rule\": \"network_proxy_lateness_p99_ms\", \"reason\": \"r\" } ]", "validity rule"),
            ("\"allowances\": [ { \"rule\": \"remote_update_rate_p5_hz\", \"add\": 2, \"reason\": \"r\" } ], \"notApplicable\": []", "has a minimum")
        })
        {
            var profile = NetworkProfile.Parse("bad", Profile(verdict: verdict), "bad.json");
            Assert.Contains(
                expected,
                Assert.Throws<StressGeneratorOptionException>(() => StressNetworkProfiles.Validate(profile)).Message,
                StringComparison.Ordinal);
        }
    }

    // The build copies the checked-in profiles below the proxy, so a name finds the same file.
    [Theory]
    [MemberData(nameof(Profiles))]
    public void AProfileNameResolvesToTheCheckedInProfile(string name)
    {
        var bundled = NetworkProfile.Resolve(name, Path.GetTempPath());
        var byPath = NetworkProfile.Resolve(Path.Combine(ProfileFolder, name + ".json"), Path.GetTempPath());

        Assert.Equal(name, bundled.Name);
        Assert.Equal(byPath.Description, bundled.Description);
        Assert.Equal(byPath.ClientToServer, bundled.ClientToServer);
        Assert.Equal(byPath.ServerToClient, bundled.ServerToClient);
        Assert.Contains(
            "does not exist",
            Assert.Throws<NetworkImpairmentException>(() => NetworkProfile.Resolve("dial-up", Path.GetTempPath())).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameNoneMeansNoProxyAndNoFileCanTakeIt()
    {
        Assert.False(NetworkProfile.IsValidName("none"));
        Assert.False(NetworkProfile.IsValidName("Normal"));
        Assert.True(NetworkProfile.IsValidName("normal-2"));
        Assert.Null(StressNetworkProfiles.LoadNamed("none", RepositoryPaths.Root));
        Assert.Throws<NetworkImpairmentException>(() => NetworkProfile.Resolve("none", RepositoryPaths.Root));
        Assert.Contains(
            "does not exist",
            Assert.Throws<StressGeneratorOptionException>(() => StressNetworkProfiles.LoadNamed("missing", RepositoryPaths.Root)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutJitterEveryPacketWaitsExactlyItsDelay()
    {
        var impairment = new DirectionImpairment(Direction(delayMs: 40), 7, Frequency);

        for (var packet = 0; packet < 1000; packet++)
        {
            var decision = impairment.Decide(packet * 1000);
            Assert.Equal(PacketFate.Forwarded, decision.Fate);
            Assert.Equal(40_000, decision.DelayTicks);
            Assert.Null(decision.DuplicateDelayTicks);
        }
    }

    [Fact]
    public void JitterSpreadsTheDelayEvenlyOverItsRange()
    {
        var impairment = new DirectionImpairment(Direction(delayMs: 100, jitterMs: 20), 7, Frequency);

        var delays = Enumerable.Range(0, 200_000).Select(packet => impairment.Decide(packet).DelayTicks / 1000d).ToArray();

        Assert.InRange(delays.Min(), 80, 80.1);
        Assert.InRange(delays.Max(), 119.9, 120);
        Assert.InRange(delays.Average(), 99.9, 100.1);
        // A uniform spread puts a quarter of the packets in each quarter of the range.
        Assert.InRange(delays.Count(delay => delay < 90) / (double)delays.Length, 0.245, 0.255);
    }

    [Theory]
    [InlineData(5, 20, 4)]
    [InlineData(10, 30, 6)]
    [InlineData(1, 0, 1)]
    public void LossAndBurstsMatchTheProfileOverManyPackets(double lossPercent, double sharePercent, double averageLength)
    {
        var burst = sharePercent > 0 ? new BurstLoss(sharePercent, averageLength) : null;
        var impairment = new DirectionImpairment(Direction(lossPercent: lossPercent, burst: burst), 1337, Frequency);
        const int packets = 2_000_000;
        long random = 0;
        long burstDropped = 0;
        long bursts = 0;
        var previousBurst = false;

        for (var packet = 0; packet < packets; packet++)
        {
            var fate = impairment.Decide(packet).Fate;
            random += fate == PacketFate.DroppedRandom ? 1 : 0;
            if (fate == PacketFate.DroppedBurst)
            {
                burstDropped++;
                bursts += previousBurst ? 0 : 1;
            }

            previousBurst = fate == PacketFate.DroppedBurst;
        }

        var lost = (random + burstDropped) * 100d / packets;
        Assert.InRange(lost, lossPercent - 0.06, lossPercent + 0.06);
        if (burst is null)
        {
            Assert.Equal(0, burstDropped);
            return;
        }

        Assert.InRange(burstDropped * 100d / (random + burstDropped), sharePercent - 1, sharePercent + 1);
        // Two bursts in a row read as one here, which a start chance of well below one percent
        // makes rare.
        Assert.InRange(burstDropped / (double)bursts, averageLength * 0.95, averageLength * 1.05);
    }

    [Fact]
    public void TheSameSeedGivesTheSameFatesAndAnotherSeedOthers()
    {
        var direction = Direction(delayMs: 100, jitterMs: 20, lossPercent: 5, duplicatePercent: 2, burst: new BurstLoss(20, 4));
        var first = Fates(new DirectionImpairment(direction, 42, Frequency));
        var again = Fates(new DirectionImpairment(direction, 42, Frequency));
        var other = Fates(new DirectionImpairment(direction, 43, Frequency));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        // Every client and direction draws from its own stream.
        Assert.NotEqual(ImpairmentRandom.DeriveSeed(1337, 1, 0), ImpairmentRandom.DeriveSeed(1337, 1, 1));
        Assert.NotEqual(ImpairmentRandom.DeriveSeed(1337, 1, 0), ImpairmentRandom.DeriveSeed(1337, 2, 0));
    }

    [Fact]
    public void DuplicationCopiesItsShareOfTheForwardedPacketsWithAJitterOfTheirOwn()
    {
        var impairment = new DirectionImpairment(Direction(delayMs: 50, jitterMs: 10, duplicatePercent: 1), 9, Frequency);

        var decisions = Enumerable.Range(0, 500_000).Select(packet => impairment.Decide(packet)).ToArray();

        var duplicated = decisions.Where(decision => decision.DuplicateDelayTicks is not null).ToArray();
        Assert.InRange(duplicated.Length * 100d / decisions.Length, 0.95, 1.05);
        Assert.All(duplicated, decision => Assert.InRange(decision.DuplicateDelayTicks!.Value, 40_000, 60_000));
        Assert.True(duplicated.Count(decision => decision.DuplicateDelayTicks != decision.DelayTicks) > duplicated.Length * 0.99);
    }

    [Fact]
    public void AnOutageDropsEveryPacketInItsWindowAndNoneOutside()
    {
        var outages = new OutageSchedule(60, 60, 3);
        var impairment = new DirectionImpairment(Direction(delayMs: 10, outages: outages), 3, Frequency);

        for (var millisecond = 0; millisecond < 200_000; millisecond += 10)
        {
            var seconds = millisecond / 1000d;
            var expected = seconds is >= 60 and < 63 or >= 120 and < 123 or >= 180 and < 183;
            var fate = impairment.Decide(millisecond * (Frequency / 1000)).Fate;
            Assert.Equal(expected ? PacketFate.DroppedOutage : PacketFate.Forwarded, fate);
        }

        Assert.False(outages.Contains(59.999));
        Assert.True(outages.Contains(60));
        Assert.True(outages.Contains(62.999));
        Assert.False(outages.Contains(63));
    }

    // With a fake clock, a packet leaves at its due time, packets due together keep their arrival
    // order, and jitter may reorder packets due at different times.
    [Fact]
    public void TheSchedulerSendsEachPacketAtItsDueTimeInArrivalOrderForTies()
    {
        long now = 0;
        var sent = new List<(string Name, long At)>();
        var counters = new DirectionCounters();
        using var scheduler = new PacketScheduler(
            packet =>
            {
                sent.Add(((string)packet.Flow, now));
                return true;
            },
            counters,
            () => now);
        var payload = new byte[] { 1, 2, 3 };

        scheduler.Schedule(DelayedPacket.Copy("a", payload, 0, Ticks(100)));
        scheduler.Schedule(DelayedPacket.Copy("b", payload, 10, Ticks(50)));
        scheduler.Schedule(DelayedPacket.Copy("c", payload, 20, Ticks(100)));
        now = Ticks(49);
        Assert.Equal(0, scheduler.Pump());
        now = Ticks(50);
        Assert.Equal(1, scheduler.Pump());
        now = Ticks(100);
        Assert.Equal(2, scheduler.Pump());

        Assert.Equal([("b", Ticks(50)), ("a", Ticks(100)), ("c", Ticks(100))], sent);
        Assert.Equal(0, scheduler.Pending);
        var counts = counters.Capture();
        Assert.Equal(3, counts.PacketsForwarded);
        Assert.Equal(9, counts.BytesForwarded);
        Assert.Equal(0, counts.LatenessMs!.Maximum);
    }

    [Fact]
    public void ThePumpCountsHowLateAPacketLeftAndASendThatFailed()
    {
        long now = 0;
        var counters = new DirectionCounters();
        var fail = false;
        using var scheduler = new PacketScheduler(_ => !fail, counters, () => now);

        scheduler.Schedule(DelayedPacket.Copy("a", [1], 0, Ticks(100)));
        now = Ticks(112);
        scheduler.Pump();
        fail = true;
        scheduler.Schedule(DelayedPacket.Copy("b", [1], now, now));
        scheduler.Pump();

        var counts = counters.Capture();
        Assert.Equal(1, counts.PacketsForwarded);
        Assert.Equal(1, counts.SendFailures);
        Assert.Equal(12, counts.LatenessMs!.Maximum, 3);
        Assert.Equal(112, counts.DelayMs!.Maximum, 3);
    }

    [Fact]
    public void ASchedulerThatFellBehindDropsBeyondItsQueueAndDiscardsTheRestAtStop()
    {
        var counters = new DirectionCounters();
        var scheduler = new PacketScheduler(_ => true, counters, () => 0, maximumQueued: 3);

        var accepted = Enumerable.Range(0, 5).Count(index => scheduler.Schedule(DelayedPacket.Copy("a", [1], 0, Ticks(10))));
        scheduler.Dispose();

        Assert.Equal(3, accepted);
        var counts = counters.Capture();
        Assert.Equal(2, counts.DroppedOverflow);
        Assert.Equal(3, counts.DiscardedAtStop);
    }

    [Fact]
    public void TheProxyRefusesAddressesOffThisMachineUnlessAFlagAllowsThem()
    {
        var loopback = new IPEndPoint(IPAddress.Loopback, 27_000);
        var remote = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 27_000);

        Assert.Contains(
            "refuses to listen",
            Assert.Throws<NetworkImpairmentException>(() => Options(remote, loopback).RequireLoopback()).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "refuses to forward",
            Assert.Throws<NetworkImpairmentException>(() => Options(loopback, remote).RequireLoopback()).Message,
            StringComparison.Ordinal);
        Assert.Throws<NetworkImpairmentException>(() => new ImpairmentProxy(Options(loopback, remote), Constant(1)));
        (Options(remote, remote) with { AllowNonLoopback = true }).RequireLoopback();
        Options(new IPEndPoint(IPAddress.IPv6Loopback, 1), loopback).RequireLoopback();
    }

    // On real loopback sockets the proxy forwards both ways without touching a payload, delays by
    // its profile, gives each client its own upstream socket, and counts what it did.
    [Fact]
    public void TheProxyForwardsBothWaysUnchangedAndCountsWhatItDid()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 5000;
        using var proxy = new ImpairmentProxy(
            Options(new IPEndPoint(IPAddress.Loopback, 0), (IPEndPoint)server.LocalEndPoint!),
            Constant(5));
        proxy.Start();
        using var first = Client(proxy);
        using var second = Client(proxy);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[256];

        for (var index = 0; index < 20; index++)
        {
            var client = index % 2 == 0 ? first : second;
            var payload = new byte[] { (byte)index, 0xAB, 0xCD, (byte)(255 - index) };
            var watch = Stopwatch.StartNew();
            client.Send(payload);
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            var length = server.ReceiveFrom(buffer, ref from);
            Assert.Equal(payload, buffer[..length]);
            sources.Add(from.ToString()!);
            server.SendTo(buffer.AsSpan(0, length), SocketFlags.None, from);
            Assert.Equal(length, client.Receive(buffer));
            Assert.Equal(payload, buffer[..length]);
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(10), $"a round trip of {watch.Elapsed.TotalMilliseconds} ms");
        }

        proxy.Dispose();
        var counts = proxy.Capture();
        Assert.Equal(2, sources.Count);
        Assert.Equal(2, counts.ClientsSeen);
        foreach (var direction in new[] { counts.ClientToServer, counts.ServerToClient })
        {
            Assert.Equal(20, direction.PacketsReceived);
            Assert.Equal(20, direction.PacketsForwarded);
            Assert.Equal(80, direction.BytesForwarded);
            Assert.Equal(0, direction.DroppedByRule);
            Assert.True(direction.DelayMs!.Minimum >= 5, $"a delay of {direction.DelayMs.Minimum} ms");
        }
    }

    [Fact]
    public void TheProxyForgetsAClientThatWasIdleForTheTimeout()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.ReceiveTimeout = 5000;
        using var proxy = new ImpairmentProxy(
            Options(new IPEndPoint(IPAddress.Loopback, 0), (IPEndPoint)server.LocalEndPoint!) with
            {
                IdleTimeout = TimeSpan.FromMilliseconds(200)
            },
            Constant(0));
        proxy.Start();
        using var client = Client(proxy);
        var buffer = new byte[16];

        client.Send(new byte[] { 1 });
        EndPoint firstSource = new IPEndPoint(IPAddress.Any, 0);
        server.ReceiveFrom(buffer, ref firstSource);
        Assert.Equal(1, proxy.ActiveClients);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (proxy.ActiveClients > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(0, proxy.ActiveClients);
        client.Send(new byte[] { 2 });
        EndPoint secondSource = new IPEndPoint(IPAddress.Any, 0);
        server.ReceiveFrom(buffer, ref secondSource);
        var counts = proxy.Capture();
        Assert.Equal(1, counts.ClientsTimedOut);
        Assert.Equal(2, counts.ClientsSeen);
        // The returning client gets a new upstream socket, so the server sees a new client.
        Assert.NotEqual(firstSource.ToString(), secondSource.ToString());
    }

    [Fact]
    public void TheCountsRoundTripThroughTheirJson()
    {
        var counters = new DirectionCounters();
        counters.RecordReceived(100);
        counters.RecordDropped(PacketFate.DroppedBurst);
        counters.RecordSent(100, 40.2, 0.1);
        var report = new ProxyCountsReport(
            "normal", 1337, DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(5), 5, "127.0.0.1:1", "127.0.0.1:2",
            1, 0, 0, 1, counters.Capture(), new DirectionCounters().Capture());

        var parsed = ProxyCountsReport.Parse(report.ToJson());

        Assert.Equal(report, parsed);
        Assert.Equal(1, parsed.ClientToServer.DroppedBurst);
        Assert.Null(parsed.ServerToClient.DelayMs);
    }

    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000d);

    private static NetworkProfile LoadShipped(string name) => NetworkProfile.Load(Path.Combine(ProfileFolder, name + ".json"));

    private static List<(PacketFate Fate, long Delay, long? Duplicate)> Fates(DirectionImpairment impairment)
    {
        return Enumerable.Range(0, 5000)
            .Select(packet => impairment.Decide(packet))
            .Select(decision => (decision.Fate, decision.DelayTicks, decision.DuplicateDelayTicks))
            .ToList();
    }

    private static DirectionProfile Direction(
        double delayMs = 10,
        double jitterMs = 0,
        double lossPercent = 0,
        double duplicatePercent = 0,
        BurstLoss? burst = null,
        OutageSchedule? outages = null)
    {
        return new DirectionProfile(delayMs, jitterMs, lossPercent, duplicatePercent, burst, outages);
    }

    private static NetworkProfile Constant(double delayMs)
    {
        return new NetworkProfile("constant", "test", Direction(delayMs), Direction(delayMs), new NetworkProfileVerdict([], []));
    }

    private static ImpairmentProxyOptions Options(IPEndPoint listen, IPEndPoint forward)
    {
        return new ImpairmentProxyOptions(listen, forward, 1, TimeSpan.FromSeconds(30), 16, false);
    }

    private static Socket Client(ImpairmentProxy proxy)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = 5000
        };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Connect(proxy.ListenEndPoint);
        return socket;
    }

    private static string Profile(string replacement = "", string verdict = "\"allowances\": [], \"notApplicable\": []")
    {
        var clientToServer = "\"clientToServer\": { \"delayMs\": 5, \"jitterMs\": 1, \"lossPercent\": 0, \"duplicatePercent\": 0 },";
        if (replacement.StartsWith("\"clientToServer\"", StringComparison.Ordinal))
        {
            clientToServer = replacement;
            replacement = string.Empty;
        }

        return $$"""
            {
              "description": "test",
              {{replacement}}
              {{clientToServer}}
              "serverToClient": { "delayMs": 5, "jitterMs": 1, "lossPercent": 0, "duplicatePercent": 0 },
              "verdict": { {{verdict}} }
            }
            """;
    }
}
