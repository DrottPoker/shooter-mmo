using System;
using Mmorpg.Client.Api;

namespace Mmorpg.Client.Core
{
    // The client session as the components that only read it see it.
    public interface IClientSession
    {
        // Raised when an account signed in; the selection and the World are empty then.
        event Action SignedIn;

        // Raised when the account session ended and the session was cleared.
        event Action SignedOut;

        // Raised when the selected character or shard changed.
        event Action SelectionChanged;

        // Raised when the worker accepted a join and the client entered its World.
        event Action WorldEntered;

        // Raised when the client left the World of its simulation session.
        event Action WorldLeft;

        ClientSettings Settings { get; }

        AuthResponse Auth { get; }

        string SessionToken { get; }

        bool IsAuthenticated { get; }

        CharacterResponse SelectedCharacter { get; }

        ShardResponse SelectedShard { get; }

        // The simulation session of the World the client is in, from the accepted join until the
        // client leaves that World; otherwise null.
        ActiveSimulationSessionResponse ActiveSimulationSession { get; }
    }

    // The client's in-memory view of its account session: a cache, not an authority. The
    // bootstrap creates it once and passes it to every component that needs it. The settings
    // never change; every other fact changes only through the transition that names it, and each
    // transition raises its one event when it changed something:
    // - SignIn sets the account and clears the selection and the World (LoginMenuPanel);
    // - SignOut clears everything (CharacterSelectPanel's logout, ClientSessionRecovery);
    // - Select sets the character and the shard (CharacterSelectPanel);
    // - EnterWorld sets the World's simulation session after an accepted join (CharacterSelectPanel);
    // - LeaveWorld clears it for the exact simulation session it names (WorldExit).
    public sealed class ClientSession : IClientSession
    {
        public ClientSession(ClientSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public event Action SignedIn;

        public event Action SignedOut;

        public event Action SelectionChanged;

        public event Action WorldEntered;

        public event Action WorldLeft;

        public ClientSettings Settings { get; private set; }

        public AuthResponse Auth { get; private set; }

        public string SessionToken
        {
            get { return Auth != null ? Auth.sessionToken : string.Empty; }
        }

        public bool IsAuthenticated
        {
            get { return !string.IsNullOrWhiteSpace(SessionToken); }
        }

        public CharacterResponse SelectedCharacter { get; private set; }

        public ShardResponse SelectedShard { get; private set; }

        public ActiveSimulationSessionResponse ActiveSimulationSession { get; private set; }

        // A new account session replaces whatever the client held before.
        public void SignIn(AuthResponse auth)
        {
            Auth = auth ?? throw new ArgumentNullException(nameof(auth));
            SelectedCharacter = null;
            SelectedShard = null;
            ActiveSimulationSession = null;
            SignedIn?.Invoke();
        }

        public void SignOut()
        {
            if (Auth == null
                && SelectedCharacter == null
                && SelectedShard == null
                && ActiveSimulationSession == null)
            {
                return;
            }

            Auth = null;
            SelectedCharacter = null;
            SelectedShard = null;
            ActiveSimulationSession = null;
            SignedOut?.Invoke();
        }

        public void Select(CharacterResponse character, ShardResponse shard)
        {
            if (ReferenceEquals(character, SelectedCharacter) && ReferenceEquals(shard, SelectedShard))
            {
                return;
            }

            SelectedCharacter = character;
            SelectedShard = shard;
            SelectionChanged?.Invoke();
        }

        // The World of an accepted join. Another accepted join replaces the session, so a join
        // after a lost connection can keep the World.
        public void EnterWorld(ActiveSimulationSessionResponse session)
        {
            ActiveSimulationSession = session ?? throw new ArgumentNullException(nameof(session));
            WorldEntered?.Invoke();
        }

        // Leaves the World only when it is still the one of this simulation session, so an exit
        // that completes late never clears a newer World. True when it left.
        public bool LeaveWorld(string simulationSessionId)
        {
            if (ActiveSimulationSession == null
                || !string.Equals(
                    ActiveSimulationSession.simulationSessionId,
                    simulationSessionId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            ActiveSimulationSession = null;
            WorldLeft?.Invoke();
            return true;
        }

        // PlayMode tests point the running client at another GameService, such as an unreachable
        // one, and back; nothing else changes the settings.
        internal void UseSettingsForTests(ClientSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }
    }
}
