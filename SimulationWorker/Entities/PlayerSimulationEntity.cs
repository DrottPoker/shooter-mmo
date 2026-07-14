using SimulationWorker.Realtime;
using SimulationWorker.Sessions;

namespace SimulationWorker.Entities;

public sealed class PlayerSimulationEntity(
    ulong networkEntityId,
    ActiveSimulationSession session,
    AuthoritativePlayerMovement movement)
{
    public const string DefaultArchetypeId = "player.default";

    public ulong NetworkEntityId { get; } = networkEntityId != 0
        ? networkEntityId
        : throw new ArgumentOutOfRangeException(nameof(networkEntityId));

    public ActiveSimulationSession Session { get; private set; } = session
        ?? throw new ArgumentNullException(nameof(session));

    public AuthoritativePlayerMovement Movement { get; } = movement
        ?? throw new ArgumentNullException(nameof(movement));

    public void RefreshSession(ActiveSimulationSession refreshedSession)
    {
        ArgumentNullException.ThrowIfNull(refreshedSession);

        if (refreshedSession.CharacterId != Session.CharacterId
            || refreshedSession.SimulationSessionId != Session.SimulationSessionId)
        {
            throw new InvalidOperationException(
                "A player entity can only refresh its existing character simulation session.");
        }

        Session = refreshedSession;
    }
}
