using WorldServer.Realtime;
using WorldServer.Sessions;

namespace WorldServer.Entities;

public sealed class PlayerWorldEntity(
    ulong networkEntityId,
    ActivePlayerSession session,
    AuthoritativePlayerMovement movement)
{
    public const string DefaultArchetypeId = "player.default";

    public ulong NetworkEntityId { get; } = networkEntityId != 0
        ? networkEntityId
        : throw new ArgumentOutOfRangeException(nameof(networkEntityId));

    public ActivePlayerSession Session { get; private set; } = session
        ?? throw new ArgumentNullException(nameof(session));

    public AuthoritativePlayerMovement Movement { get; } = movement
        ?? throw new ArgumentNullException(nameof(movement));

    public void RefreshSession(ActivePlayerSession refreshedSession)
    {
        ArgumentNullException.ThrowIfNull(refreshedSession);

        if (refreshedSession.CharacterId != Session.CharacterId
            || refreshedSession.WorldSessionId != Session.WorldSessionId)
        {
            throw new InvalidOperationException(
                "A player entity can only refresh its existing character world session.");
        }

        Session = refreshedSession;
    }
}
