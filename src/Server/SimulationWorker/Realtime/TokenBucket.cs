using Mmorpg.SimulationWorker.Config;

namespace Mmorpg.SimulationWorker.Realtime;

// Refills on the timestamps of its time provider, the system's by default; the replication
// benchmark gives it a clock that advances one tick at a time.
public sealed class TokenBucket
{
    private readonly double tokensPerSecond;
    private readonly double capacity;
    private readonly TimeProvider timeProvider;
    private double availableTokens;
    private long lastTimestamp;

    public TokenBucket(double tokensPerSecond, double capacity, TimeProvider? timeProvider = null)
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
        this.timeProvider = timeProvider ?? TimeProvider.System;
        availableTokens = capacity;
        lastTimestamp = this.timeProvider.GetTimestamp();
    }

    public bool TryConsume(double amount)
    {
        return TryConsume(amount, timeProvider.GetTimestamp());
    }

    public bool TryConsume(double amount, long timestamp)
    {
        if (amount <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        var elapsedTicks = Math.Max(0L, timestamp - lastTimestamp);
        var elapsedSeconds = elapsedTicks / (double)timeProvider.TimestampFrequency;
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

    public UdpPeerQuota(UdpQuotaConfig config, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        inboundPackets = new TokenBucket(
            config.InboundPacketsPerSecond,
            config.InboundPacketBurst,
            timeProvider);
        inboundBytes = new TokenBucket(
            config.InboundBytesPerSecond,
            config.InboundByteBurst,
            timeProvider);
        snapshotBytes = new TokenBucket(
            config.SnapshotBytesPerSecond,
            config.SnapshotByteBurst,
            timeProvider);
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
