using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Mmorpg.GameProtocol;
using Mmorpg.SharedTests;
using Mmorpg.Testing;

namespace Mmorpg.Shared.Tests;

// The sealed realtime transport on .NET: the shared cases, which the Unity tests and the IL2CPP
// test player run too, against both cipher implementations, and what only .NET has: the
// platform's ChaCha20-Poly1305 and HKDF to check the managed code against.
public sealed class RealtimeTransportSecurityTests
{
    [Fact]
    public void ThePlatformImplementationIsSupportedHere()
    {
        Assert.True(RealtimeAead.IsPlatformSupported, "The test machine's .NET offers no ChaCha20-Poly1305.");
        Assert.Equal(RealtimePlatformChaCha20Poly1305.Name, RealtimeAead.DefaultImplementationName);
    }

    [Fact]
    public void TheManagedImplementationMeetsTheRfc8439Vectors()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyAeadVectors(key => new RealtimeManagedChaCha20Poly1305(key)));
    }

    [Fact]
    public void ThePlatformImplementationMeetsTheRfc8439Vectors()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyAeadVectors(key => new RealtimePlatformChaCha20Poly1305(key)));
    }

    [Fact]
    public void BothImplementationsSealAndOpenRandomInputsAlike()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyImplementationsAgree(
            key => new RealtimeManagedChaCha20Poly1305(key),
            key => new RealtimePlatformChaCha20Poly1305(key),
            seed: 8_1,
            rounds: 2_000));
    }

    [Fact]
    public void HkdfMeetsTheRfc5869VectorsAndThePlatform()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyHkdfVectors());
        var random = new Random(5869);
        for (var round = 0; round < 200; round++)
        {
            var inputKeyMaterial = RandomBytes(random, random.Next(1, 80));
            var salt = RandomBytes(random, random.Next(0, 80));
            var info = RandomBytes(random, random.Next(0, 80));
            var length = random.Next(1, 255 * 32);
            Assert.Equal(
                HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, length, salt, info),
                RealtimeHkdf.DeriveKey(inputKeyMaterial, length, salt, info));
        }
    }

    [Fact]
    public void TheKeyScheduleTheProofAndTheDatagramFormatArePinned()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyPinnedVectors());
    }

    [Fact]
    public void AJoinProofVerifiesOnlyWithItsOwnInputs()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyJoinProof());
    }

    [Fact]
    public void TheReplayWindowTakesEveryCounterOnceInAnyOrder()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyReplayWindow());
    }

    [Fact]
    public void TheLayersSealAfterTheSwitchAndDropWhatDoesNotAuthenticate()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyLayers());
    }

    [Fact]
    public void CountersNeverRepeatOverALongRun()
    {
        AssertNoFailures(RealtimeTransportSecurityCases.VerifyCountersNeverRepeat(200_000));
    }

    // LiteNetLib seals from its update thread and from every thread that sends at once, so the
    // counters of concurrent seals are still used once each.
    [Fact]
    public void ConcurrentSealsUseEveryCounterOnce()
    {
        using var keys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey());
        using var session = RealtimeSealedSession.ForWorker(keys);
        var counters = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        Parallel.For(0, 8, _ =>
        {
            var buffer = new byte[100];
            for (var index = 0; index < 5_000; index++)
            {
                var length = 50;
                Assert.True(session.TrySeal(buffer, 0, ref length));
                Assert.True(RealtimeDatagram.TryReadCounter(buffer, length, out var counter));
                counters.Add(counter);
            }
        });

        Assert.Equal(40_000, counters.Count);
        Assert.Equal(40_000, counters.Distinct().Count());
        Assert.Equal(39_999UL, counters.Max());
    }

    // A disposed session seals and opens nothing, and its keys are zero.
    [Fact]
    public void ADisposedSessionSealsAndOpensNothing()
    {
        var keys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey());
        var session = RealtimeSealedSession.ForWorker(keys);
        var buffer = new byte[100];
        var length = 40;
        Assert.True(session.TrySeal(buffer, 0, ref length));
        session.Dispose();
        keys.Dispose();

        var next = 40;
        Assert.False(session.TrySeal(buffer, 0, ref next));
        Assert.Equal(RealtimeOpenResult.Closed, session.TryOpen(buffer, ref length));
        Assert.All(keys.ProofKey.Concat(keys.ClientToWorkerKey).Concat(keys.WorkerToClientKey), value => Assert.Equal(0, value));
    }

    // The worker finds a peer's keys by address and port, whatever object names the endpoint.
    [Fact]
    public void TheWorkerFindsKeysByAddressAndPort()
    {
        using var keys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey());
        using var security = new RealtimeWorkerTransportSecurity();
        security.Install(new IPEndPoint(IPAddress.Parse("::1"), 5000), keys);

        Assert.True(security.IsSealed(new IPEndPoint(IPAddress.IPv6Loopback, 5000)));
        Assert.False(security.IsSealed(new IPEndPoint(IPAddress.IPv6Loopback, 5001)));
        Assert.False(security.IsSealed(new IPEndPoint(IPAddress.Loopback, 5000)));
        Assert.Equal(1, security.SealedPeerCount);
        security.Remove(new IPEndPoint(IPAddress.IPv6Loopback, 5000));
        Assert.Equal(0, security.SealedPeerCount);
    }

    // The cost of the cipher itself: nanoseconds to seal and to open one datagram of 60, 300, and
    // 1,200 bytes with each implementation, written to the test output.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Benchmark)]
    public void MeasureTheCipherPerDatagram()
    {
        var lines = new List<string>
        {
            $"{"implementation",-10} {"bytes",6} {"seal ns",9} {"open ns",9}"
        };
        foreach (var (name, create) in new (string, Func<byte[], IRealtimeAead>)[]
                 {
                     (RealtimePlatformChaCha20Poly1305.Name, key => new RealtimePlatformChaCha20Poly1305(key)),
                     (RealtimeManagedChaCha20Poly1305.Name, key => new RealtimeManagedChaCha20Poly1305(key))
                 })
        {
            foreach (var size in new[] { 60, 300, 1_200 })
            {
                var (seal, open) = MeasureDatagram(create, size);
                lines.Add($"{name,-10} {size,6} {seal,9:F0} {open,9:F0}");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, lines));
        var path = Environment.GetEnvironmentVariable("MMORPG_CIPHER_BENCHMARK_RESULT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllLines(path, lines);
        }
    }

    // Seals and opens the same datagram over and over through the sealed session, as the packet
    // layer does, and takes the best of five runs per direction.
    private static (double SealNanoseconds, double OpenNanoseconds) MeasureDatagram(
        Func<byte[], IRealtimeAead> create,
        int size)
    {
        const int Datagrams = 20_000;
        using var keys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey());
        using var sender = RealtimeSealedSession.ForClient(keys, create);
        var buffer = new byte[size + RealtimeDatagram.MaximumOverhead];
        var sealedDatagrams = new byte[Datagrams][];
        var bestSeal = double.MaxValue;
        var bestOpen = double.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            for (var index = 0; index < Datagrams; index++)
            {
                var length = size;
                sender.TrySeal(buffer, 0, ref length);
                if (run == 0)
                {
                    sealedDatagrams[index] = buffer[..length];
                }
            }

            bestSeal = Math.Min(bestSeal, stopwatch.Elapsed.TotalNanoseconds / Datagrams);

            // A fresh receiver per run, so its replay window accepts the same counters again.
            using var receiver = RealtimeSealedSession.ForWorker(keys, create);
            var open = new byte[buffer.Length];
            stopwatch.Restart();
            for (var index = 0; index < Datagrams; index++)
            {
                var datagram = sealedDatagrams[index];
                Buffer.BlockCopy(datagram, 0, open, 0, datagram.Length);
                var length = datagram.Length;
                if (receiver.TryOpen(open, ref length) != RealtimeOpenResult.Opened)
                {
                    throw new InvalidOperationException("A measured datagram did not open.");
                }
            }

            bestOpen = Math.Min(bestOpen, stopwatch.Elapsed.TotalNanoseconds / Datagrams);
        }

        return (bestSeal, bestOpen);
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    private static void AssertNoFailures(IReadOnlyList<string> failures)
    {
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
