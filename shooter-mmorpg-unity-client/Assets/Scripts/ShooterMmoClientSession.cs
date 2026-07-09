using ShooterMmo.Api;

namespace ShooterMmo
{
    public static class ShooterMmoClientSession
    {
        public static string AuthServiceBaseUrl = "http://localhost:5000";
        public static string WorldServerBaseUrl = "http://localhost:5100";
        public static AuthResponse Auth;
        public static CharacterResponse SelectedCharacter;
        public static WorldResponse SelectedWorld;
        public static ActivePlayerSessionResponse ActiveWorldSession;

        public static string SessionToken
        {
            get { return Auth != null ? Auth.sessionToken : string.Empty; }
        }

        public static bool IsAuthenticated
        {
            get { return !string.IsNullOrWhiteSpace(SessionToken); }
        }

        public static void Clear()
        {
            Auth = null;
            SelectedCharacter = null;
            SelectedWorld = null;
            ActiveWorldSession = null;
        }
    }
}

