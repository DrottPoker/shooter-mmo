using System.Diagnostics;
using SimulationWorker.Config;

namespace SimulationWorker.Realtime;

public sealed class TokenBucket
{
    private readonly double tokensPerSecond;
    private readonly double capacity;
    private double availableTokens;
    private long lastTimestamp;

    public TokenBucket(double tokensPerSecond, double capacity)
    {
        if (tokensPerSecond <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(tokensPerSecond));
        }

        if (capacity <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.tokensPerSecond = tokensPerSecond;
        this.capacity = capacity;
        availableTokens = capacity;
        lastTimestamp = Stopwatch.GetTimestamp();
    }

    public bool TryConsume(double amount)
    {
        return TryConsume(amount, Stopwatch.GetTimestamp());
    }

    public bool TryConsume(double amount, long timestamp)
    {
        if (amount <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var elapsedTicks = Math.Max(0L, timestamp - lastTimestamp);
        var elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        availableTokens = Math.Min(
            capacity,
            availableTokens + (elapsedSeconds * tokensPerSecond));
        lastTimestamp = Math.Max(lastTimestamp, timestamp);
        if (availableTokens < amount)
        {
            return false;
        }

        availableTokens -= amount;
        return true;
    }
}

public sealed class UdpPeerQuota
{
    private readonly TokenBucket inboundPackets;
    private readonly TokenBucket inboundBytes;
    private readonly TokenBucket snapshotBytes;

    public UdpPeerQuota(UdpQuotaConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        inboundPackets = new TokenBucket(
            config.InboundPacketsPerSecond,
            config.InboundPacketBurst);
        inboundBytes = new TokenBucket(
            config.InboundBytesPerSecond,
            config.InboundByteBurst);
        snapshotBytes = new TokenBucket(
            config.SnapshotBytesPerSecond,
            config.SnapshotByteBurst);
    }

    public bool TryConsumeInbound(int packetBytes)
    {
        return packetBytes > 0
            && inboundPackets.TryConsume(1d)
            && inboundBytes.TryConsume(packetBytes);
    }

    public bool TryConsumeSnapshot(int packetBytes)
    {
        return packetBytes > 0 && snapshotBytes.TryConsume(packetBytes);
    }
}
