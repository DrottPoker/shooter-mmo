using ShooterMmo.Api;

namespace ShooterMmo
{
    public static class ShooterMmoClientSession
    {
        public static string AuthServiceBaseUrl { get; private set; } = "http://localhost:5000";
        public static string WorldServerBaseUrl { get; private set; } = "http://localhost:5100";
        public static int RequestTimeoutSeconds { get; private set; } = 10;
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

        public static void Configure(Config.ShooterMmoClientConfig config)
        {
            if (config == null)
            {
                return;
            }

            AuthServiceBaseUrl = config.AuthServiceBaseUrl;
            WorldServerBaseUrl = config.WorldServerBaseUrl;
            RequestTimeoutSeconds = config.RequestTimeoutSeconds;
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
