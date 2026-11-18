using System.Diagnostics;
using Mmorpg.GameProtocol;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.DeploySmokeTest;

// Walks a square of eight meter legs from where the bot stands, a new leg every few seconds, so a
// bot that moves for any length of time stays near its spawn. It records how far the worker's
// authoritative position got from the first one, which is what proves that the inputs reached the
// worker and the worker moved the character.
public sealed class WalkingInputSource(int botIndex) : ISimulationBotInputSource
{
    private const float LegMeters = 8f;
    private const double LegSeconds = 2.5;

    private SimulationBotPosition? start;
    private RealtimeMovementPath? current;
    private uint nextLegTick;
    private uint pathId;
    private int leg;

    public double FarthestMeters { get; private set; }

    public RealtimeMovementPath? SelectPath(
        uint clientTick,
        RealtimeJoinAccepted joinedSession,
        SimulationBotPosition position)
    {
        start ??= position;
        FarthestMeters = Math.Max(
            FarthestMeters,
            Math.Sqrt(Math.Pow(position.X - start.Value.X, 2) + Math.Pow(position.Z - start.Value.Z, 2)));
        if (current is null || clientTick >= nextLegTick)
        {
            var settings = joinedSession.MovementSettings;
            // East, north, west, south, and again, each bot starting on its own side.
            var side = (leg + botIndex) % 4;
            var (directionX, directionZ) = side switch
            {
                0 => (1f, 0f),
                1 => (0f, 1f),
                2 => (-1f, 0f),
                _ => (0f, -1f)
            };
            var x = Math.Clamp(position.X + (directionX * LegMeters), settings.MinimumX + 1f, settings.MaximumX - 1f);
            var z = Math.Clamp(position.Z + (directionZ * LegMeters), settings.MinimumZ + 1f, settings.MaximumZ - 1f);
            pathId++;
            current = new RealtimeMovementPath(pathId, [new RealtimeMovementPathWaypoint(x, position.Y, z)]);
            nextLegTick = clientTick + (uint)Math.Round(LegSeconds * settings.TickRateHz);
            leg++;
        }

        return current;
    }
}

// One player's UDP connection: the bot client library with a walking input source.
public sealed class RealtimeBot : IDisposable
{
    private readonly HeadlessSimulationBotClient client;

    public RealtimeBot(int index, string name, SimulationBotIdentity identity, string host, int udpPort)
    {
        Index = index;
        Name = name;
        Walker = new WalkingInputSource(index);
        client = new HeadlessSimulationBotClient(
            new SimulationBotConnectionOptions(host, udpPort, TimeSpan.FromSeconds(15)),
            identity,
            Walker);
    }

    public int Index { get; }

    public string Name { get; }

    public WalkingInputSource Walker { get; }

    public SimulationBotClientState State => client.State;

    public SimulationBotClientSnapshot Capture() => client.Capture();

    public void Start() => client.Start();

    public void Poll() => client.Poll(Stopwatch.GetTimestamp());

    public void BeginLeave() => client.BeginLeave(Stopwatch.GetTimestamp());

    public void Dispose() => client.Dispose();

    // Polls every bot until the condition holds or the time is up, and returns whether it held.
    // A bot that failed ends the wait too: nothing it could still do would satisfy the caller.
    public static async Task<bool> PollUntilAsync(
        IReadOnlyCollection<RealtimeBot> bots,
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline || bots.Any(bot => bot.State == SimulationBotClientState.Failed))
            {
                return condition();
            }

            foreach (var bot in bots)
            {
                bot.Poll();
            }

            await Task.Delay(5, cancellationToken);
        }

        return true;
    }
}
