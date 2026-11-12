using Mmorpg.SimulationWorker.Combat;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Realtime;

// The entity of a character whose connection ended while its simulation session goes on. It
// stays in the world without a peer until its end tick, which every hit it takes pushes on,
// unless the player comes back, it dies, or its session ends. Loop-confined.
internal sealed class LingeringBody(PlayerSimulationEntity entity, ActiveSimulationSession session, uint endTick)
{
    public PlayerSimulationEntity Entity { get; } = entity;

    // The session generation the body belongs to. A reconnect whose client went before its join
    // finished renews it.
    public ActiveSimulationSession Session { get; set; } = session;

    public uint EndTick { get; set; } = endTick;

    // Set when the body goes at the next pass whatever its end tick, such as after its death
    // committed.
    public bool EndRequested { get; set; }
}

// The lingering bodies of the worker, by character and by entity, in the order they formed. A
// body lasts the configured time after its connection ended and after every hit it takes.
// Loop-confined.
internal sealed class LingeringBodies(int seconds, int tickRateHz, RealtimeSimulationClock clock)
{
    private readonly Dictionary<Guid, LingeringBody> byCharacter = [];
    private readonly Dictionary<ulong, LingeringBody> byEntity = [];
    private readonly List<LingeringBody> ordered = [];

    // How long a body stays after its connection ended or its last hit.
    public int Seconds { get; } = seconds > 0 ? seconds : throw new ArgumentOutOfRangeException(nameof(seconds));

    public uint DurationTicks { get; } = CombatTicks.FromSeconds(seconds, tickRateHz);

    public int Count => ordered.Count;

    // The entity of the session stays in the world from now on, without a peer.
    public LingeringBody Begin(PlayerSimulationEntity entity, ActiveSimulationSession session)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(session);
        Remove(entity.NetworkEntityId);
        var body = new LingeringBody(entity, session, clock.ServerTick + DurationTicks);
        byCharacter[session.CharacterId] = body;
        byEntity[entity.NetworkEntityId] = body;
        ordered.Add(body);
        return body;
    }

    public bool TryGet(Guid characterId, out LingeringBody body)
    {
        return byCharacter.TryGetValue(characterId, out body!);
    }

    public bool IsBody(ulong entityId)
    {
        return byEntity.ContainsKey(entityId);
    }

    // True while a body of the session is in the world, whichever generation it holds.
    public bool Holds(ActiveSimulationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return byCharacter.TryGetValue(session.CharacterId, out var body)
            && body.Session.SimulationSessionId == session.SimulationSessionId;
    }

    // A hit at the server tick keeps the body at least the full time after it.
    public void ExtendAfterHit(ulong entityId, uint serverTick)
    {
        if (ordered.Count == 0 || !byEntity.TryGetValue(entityId, out var body))
        {
            return;
        }

        var endTick = serverTick + DurationTicks;
        if (!CombatTicks.IsReached(body.EndTick, endTick))
        {
            body.EndTick = endTick;
        }
    }

    // The body of the session goes at the next pass, such as when its death committed.
    public void RequestEnd(ActiveSimulationSession session)
    {
        if (ordered.Count > 0 && Holds(session))
        {
            byCharacter[session.CharacterId].EndRequested = true;
        }
    }

    public bool Remove(ulong entityId)
    {
        if (!byEntity.Remove(entityId, out var body))
        {
            return false;
        }

        byCharacter.Remove(body.Session.CharacterId);
        ordered.Remove(body);
        return true;
    }

    // Fills the buffer with the bodies in the order they formed, so a pass may end some of them.
    public void CopyTo(List<LingeringBody> buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        buffer.Clear();
        buffer.AddRange(ordered);
    }

    public void Clear()
    {
        byCharacter.Clear();
        byEntity.Clear();
        ordered.Clear();
    }
}
