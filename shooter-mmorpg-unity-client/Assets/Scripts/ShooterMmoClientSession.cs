using ShooterMmo.Api;
using UnityEngine;

namespace ShooterMmo
{
    public static class ShooterMmoClientSession
    {
        private const string DefaultAuthServiceBaseUrl = "http://localhost:5000";
        private const int DefaultRequestTimeoutSeconds = 10;
        private const int DefaultRealtimeTimeoutSeconds = 10;
        private const int DefaultSessionValidationIntervalSeconds = 5;

        public static string AuthServiceBaseUrl { get; private set; } = DefaultAuthServiceBaseUrl;
        public static int RequestTimeoutSeconds { get; private set; } = DefaultRequestTimeoutSeconds;
        public static int RealtimeTimeoutSeconds { get; private set; } = DefaultRealtimeTimeoutSeconds;
        public static int SessionValidationIntervalSeconds { get; private set; } =
            DefaultSessionValidationIntervalSeconds;
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

        // Static state survives entering Play Mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetStaticState()
        {
            Clear();
            AuthServiceBaseUrl = DefaultAuthServiceBaseUrl;
            RequestTimeoutSeconds = DefaultRequestTimeoutSeconds;
            RealtimeTimeoutSeconds = DefaultRealtimeTimeoutSeconds;
            SessionValidationIntervalSeconds = DefaultSessionValidationIntervalSeconds;
        }
    }
}
