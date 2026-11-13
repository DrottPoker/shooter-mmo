using System;

namespace Mmorpg.Client.Core
{
    // The client settings that the bootstrap reads from the configuration once: where GameService
    // is, the request and realtime timeouts, and how often the account session is checked. They
    // never change afterwards.
    public sealed class ClientSettings
    {
        public ClientSettings(
            string gameServiceBaseUrl,
            int requestTimeoutSeconds,
            int realtimeTimeoutSeconds,
            int sessionValidationIntervalSeconds)
        {
            if (string.IsNullOrWhiteSpace(gameServiceBaseUrl))
            {
                throw new ArgumentException("A GameService base URL is required.", nameof(gameServiceBaseUrl));
            }

            if (requestTimeoutSeconds <= 0 || realtimeTimeoutSeconds <= 0 || sessionValidationIntervalSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestTimeoutSeconds),
                    "Every timeout and interval must be positive.");
            }

            GameServiceBaseUrl = gameServiceBaseUrl;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            RealtimeTimeoutSeconds = realtimeTimeoutSeconds;
            SessionValidationIntervalSeconds = sessionValidationIntervalSeconds;
        }

        public string GameServiceBaseUrl { get; }

        public int RequestTimeoutSeconds { get; }

        public int RealtimeTimeoutSeconds { get; }

        public int SessionValidationIntervalSeconds { get; }

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
                config.SessionValidationIntervalSeconds);
        }

        // The same settings with another GameService, such as an unreachable one in a test.
        public ClientSettings WithGameServiceBaseUrl(string gameServiceBaseUrl)
        {
            return new ClientSettings(
                gameServiceBaseUrl,
                RequestTimeoutSeconds,
                RealtimeTimeoutSeconds,
                SessionValidationIntervalSeconds);
        }
    }
}
