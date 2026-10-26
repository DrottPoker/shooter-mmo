using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Entities;

public sealed class PlayerSimulationEntity(
    ulong networkEntityId,
    ActiveSimulationSession session,
    AuthoritativePlayerMovement movement)
{
    public ulong NetworkEntityId { get; } = networkEntityId != 0
        ? networkEntityId
        : throw new ArgumentOutOfRangeException(nameof(networkEntityId));

    public ActiveSimulationSession Session { get; private set; } = session
        ?? throw new ArgumentNullException(nameof(session));

    public AuthoritativePlayerMovement Movement { get; } = movement
        ?? throw new ArgumentNullException(nameof(movement));

    // A development bot presents differently, so players can tell it from a person.
    public string ArchetypeId => Session.IsSyntheticBot
        ? RealtimePlayerArchetypeIds.DevelopmentBot
        : RealtimePlayerArchetypeIds.Player;

    // The equipment slot whose item the player uses now, such as tool while it gathers, or empty;
    // its look shows that item in its hands.
    public string UsedEquipmentSlot { get; set; } = string.Empty;

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
