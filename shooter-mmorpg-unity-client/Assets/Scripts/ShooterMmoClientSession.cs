using ShooterMmo.Api;

namespace ShooterMmo
{
    public static class ShooterMmoClientSession
    {
        public static string AuthServiceBaseUrl { get; private set; } = "http://localhost:5000";
        public static int RequestTimeoutSeconds { get; private set; } = 10;
        public static int RealtimeTimeoutSeconds { get; private set; } = 10;
        public static int SessionValidationIntervalSeconds { get; private set; } = 5;
        public static AuthResponse Auth;
        public static CharacterResponse SelectedCharacter;
        public static ShardResponse SelectedShard;
        public static ActiveSimulationSessionResponse ActiveSimulationSession;

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
            RequestTimeoutSeconds = config.RequestTimeoutSeconds;
            RealtimeTimeoutSeconds = config.RealtimeTimeoutSeconds;
            SessionValidationIntervalSeconds = config.SessionValidationIntervalSeconds;
        }

        public static void Clear()
        {
            Auth = null;
            SelectedCharacter = null;
            SelectedShard = null;
            ActiveSimulationSession = null;
        }
    }
}
