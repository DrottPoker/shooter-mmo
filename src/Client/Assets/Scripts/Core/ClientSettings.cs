using System;

namespace Mmorpg.Client.Core
{
    // The client settings that the bootstrap reads from the configuration once: where GameService
    // is, the request and realtime timeouts, how often the account session is checked, and how
    // long the client tries to restore a lost connection. They never change afterwards.
    public sealed class ClientSettings
    {
        // Mirrors the 20 seconds that SimulationWorker keeps a character's body after its
        // connection ended. The server decides how long the body stays; this is only how long the
        // client tries before it gives up.
        public const int DefaultReconnectWindowSeconds = 20;

        public ClientSettings(
            string gameServiceBaseUrl,
            int requestTimeoutSeconds,
            int realtimeTimeoutSeconds,
            int sessionValidationIntervalSeconds,
            int reconnectWindowSeconds = DefaultReconnectWindowSeconds)
        {
            if (string.IsNullOrWhiteSpace(gameServiceBaseUrl))
            {
                throw new ArgumentException("A GameService base URL is required.", nameof(gameServiceBaseUrl));
            }

            if (requestTimeoutSeconds <= 0
                || realtimeTimeoutSeconds <= 0
                || sessionValidationIntervalSeconds <= 0
                || reconnectWindowSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestTimeoutSeconds),
                    "Every timeout and interval must be positive.");
            }

            GameServiceBaseUrl = gameServiceBaseUrl;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            RealtimeTimeoutSeconds = realtimeTimeoutSeconds;
            SessionValidationIntervalSeconds = sessionValidationIntervalSeconds;
            ReconnectWindowSeconds = reconnectWindowSeconds;
        }

        public string GameServiceBaseUrl { get; }

        public int RequestTimeoutSeconds { get; }

        public int RealtimeTimeoutSeconds { get; }

        public int SessionValidationIntervalSeconds { get; }

        // How long after a lost connection the client tries to join the World again.
        public int ReconnectWindowSeconds { get; }

        public static ClientSettings From(ClientConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return new ClientSettings(
                config.GameServiceBaseUrl,
                config.RequestTimeoutSeconds,
                config.RealtimeTimeoutSeconds,
                config.SessionValidationIntervalSeconds,
                config.ReconnectWindowSeconds);
        }

        // The same settings with another GameService, such as an unreachable one in a test.
        public ClientSettings WithGameServiceBaseUrl(string gameServiceBaseUrl)
        {
            return new ClientSettings(
                gameServiceBaseUrl,
                RequestTimeoutSeconds,
                RealtimeTimeoutSeconds,
                SessionValidationIntervalSeconds,
                ReconnectWindowSeconds);
        }
    }
}
