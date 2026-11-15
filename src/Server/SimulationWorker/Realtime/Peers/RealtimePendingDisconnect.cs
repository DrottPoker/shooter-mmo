namespace Mmorpg.SimulationWorker.Realtime;

// A disconnect that waits for the client to acknowledge the reliable message that says why its
// connection ends. The transport passes it as the token of that message's delivery event, and
// the disconnect carries the message as its data either way. Loop-confined, but the delivery
// event that acknowledges it also runs on the loop, inside the transport poll.
internal sealed class RealtimePendingDisconnect(byte[]? data, DateTime requestedAtUtc)
{
    // The encoded message that the disconnect carries, fitted to the transport's limit, or null.
    public byte[]? Data { get; } = data;

    public DateTime RequestedAtUtc { get; } = requestedAtUtc;

    // The client acknowledged the reliable message, or none was sent, so nothing comes back.
    public bool Acknowledged { get; private set; }

    // Set by the first supervision after the request, so the disconnect goes no sooner than the
    // next loop iteration.
    public bool Supervised { get; private set; }

    public void MarkAcknowledged()
    {
        Acknowledged = true;
    }

    // Whether the disconnect goes now: once acknowledged, or once the wait reached its maximum.
    // The first call only marks the request as supervised.
    public bool IsDue(DateTime utcNow, TimeSpan maximumWait)
    {
        if (!Supervised)
        {
            Supervised = true;
            return false;
        }

        return Acknowledged || utcNow - RequestedAtUtc >= maximumWait;
    }
}
