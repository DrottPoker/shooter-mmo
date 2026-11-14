namespace Mmorpg.Tools.NetworkImpairmentProxy;

public enum PacketFate
{
    Forwarded,
    DroppedRandom,
    DroppedBurst,
    DroppedOutage
}

// The fate of one packet. A forwarded packet leaves after DelayTicks, and its copy, when the
// packet is duplicated, after DuplicateDelayTicks; both are Stopwatch ticks after its arrival.
public readonly record struct ImpairmentDecision(PacketFate Fate, long DelayTicks, long? DuplicateDelayTicks)
{
    public bool IsForwarded => Fate == PacketFate.Forwarded;
}

// Decides what happens to each packet of one direction of one client. Every packet draws the
// same number of random numbers whatever its fate, so the fate of the n-th packet depends only
// on the seed and n, and on its arrival time only for an outage. Not thread-safe: one receive
// loop owns each direction of a client.
public sealed class DirectionImpairment
{
    private readonly DirectionProfile profile;
    private readonly ImpairmentRandom random;
    private readonly double timestampFrequency;
    private readonly double randomLoss;
    private readonly double burstStart;
    private readonly double burstEnd;
    private readonly double duplicate;
    private bool inBurst;

    public DirectionImpairment(DirectionProfile profile, ulong seed, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        this.profile = profile;
        random = new ImpairmentRandom(seed);
        this.timestampFrequency = timestampFrequency;
        var loss = profile.LossPercent / 100d;
        duplicate = profile.DuplicatePercent / 100d;
        if (profile.BurstLoss is { } burst)
        {
            // The share b of all packets that bursts drop, and the bursts' mean length L. A burst
            // starts on a packet outside a burst with probability s and ends after each of its
            // packets with probability 1/L, which drops b = sL / (1 + s(L - 1)) of all packets,
            // so s = b / (L(1 - b) + b). The rest of the loss falls on the packets outside bursts.
            var share = loss * burst.SharePercent / 100d;
            var length = burst.AverageLengthPackets;
            burstStart = share / ((length * (1d - share)) + share);
            burstEnd = 1d / length;
            randomLoss = (loss - share) / (1d - share);
        }
        else
        {
            randomLoss = loss;
        }
    }

    public ImpairmentDecision Decide(long ticksSinceFlowStart)
    {
        // Always six draws, in this order, so the sequence of fates follows the packet count.
        var startDraw = random.NextDouble();
        var endDraw = random.NextDouble();
        var lossDraw = random.NextDouble();
        var jitterDraw = random.NextDouble();
        var duplicateDraw = random.NextDouble();
        var duplicateJitterDraw = random.NextDouble();

        var fate = PacketFate.Forwarded;
        if (!inBurst && startDraw < burstStart)
        {
            inBurst = true;
        }

        if (inBurst)
        {
            fate = PacketFate.DroppedBurst;
            if (endDraw < burstEnd)
            {
                inBurst = false;
            }
        }
        else if (lossDraw < randomLoss)
        {
            fate = PacketFate.DroppedRandom;
        }

        // The line is down whatever the other rules decided.
        if (profile.Outages is { } outages && outages.Contains(ticksSinceFlowStart / timestampFrequency))
        {
            fate = PacketFate.DroppedOutage;
        }

        if (fate != PacketFate.Forwarded)
        {
            return new ImpairmentDecision(fate, 0, null);
        }

        return new ImpairmentDecision(
            PacketFate.Forwarded,
            DelayTicks(jitterDraw),
            duplicateDraw < duplicate ? DelayTicks(duplicateJitterDraw) : null);
    }

    private long DelayTicks(double jitterDraw)
    {
        var milliseconds = profile.DelayMs + (((jitterDraw * 2d) - 1d) * profile.JitterMs);
        return (long)Math.Round(Math.Max(0d, milliseconds) * timestampFrequency / 1000d);
    }
}

// xoshiro256** seeded through SplitMix64: fast, and the same sequence on every runtime for a seed.
public sealed class ImpairmentRandom
{
    private ulong s0;
    private ulong s1;
    private ulong s2;
    private ulong s3;

    public ImpairmentRandom(ulong seed)
    {
        var state = seed;
        s0 = SplitMix(ref state);
        s1 = SplitMix(ref state);
        s2 = SplitMix(ref state);
        s3 = SplitMix(ref state);
    }

    // The seed of one direction of one client: the run's seed, the client's number in the order
    // the proxy first heard from it, and the direction.
    public static ulong DeriveSeed(long runSeed, int clientNumber, int direction)
    {
        var state = unchecked((ulong)runSeed);
        var mixed = SplitMix(ref state) ^ unchecked((ulong)clientNumber * 0x9E3779B97F4A7C15UL);
        mixed = SplitMix(ref mixed) ^ unchecked((ulong)(direction + 1) * 0xC2B2AE3D27D4EB4FUL);
        return SplitMix(ref mixed);
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            var result = RotateLeft(s1 * 5, 7) * 9;
            var t = s1 << 17;
            s2 ^= s0;
            s3 ^= s1;
            s1 ^= s2;
            s0 ^= s3;
            s2 ^= t;
            s3 = RotateLeft(s3, 45);
            return result;
        }
    }

    // Evenly in [0, 1) with 53 bits.
    public double NextDouble()
    {
        return (NextUInt64() >> 11) * (1d / (1UL << 53));
    }

    private static ulong SplitMix(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static ulong RotateLeft(ulong value, int count)
    {
        return (value << count) | (value >> (64 - count));
    }
}
