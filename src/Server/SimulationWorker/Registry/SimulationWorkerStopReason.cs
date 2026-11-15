namespace Mmorpg.SimulationWorker.Registry;

// Why the worker stops, which every connected player is told before the transport stops. The
// first reason given wins, since a later stop request only follows from the first. Without one
// the stop is a normal shutdown. Thread-safe.
public sealed class SimulationWorkerStopReason
{
    public const string ServerShutdownCode = "server_shutdown";
    public const string ServerShutdownMessage = "The shard is shutting down.";

    private static readonly Reason Shutdown = new(ServerShutdownCode, ServerShutdownMessage);

    private Reason reason = Shutdown;

    public string Code => Volatile.Read(ref reason).Code;

    public string Message => Volatile.Read(ref reason).Message;

    public void Set(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Interlocked.CompareExchange(ref reason, new Reason(code, message), Shutdown);
    }

    private sealed record Reason(string Code, string Message);
}
