using System;

namespace Mmorpg.Client.Core
{
    // The client settings that the bootstrap reads once at start: the environment and the build
    // stamp of this client, where GameService is, the request and realtime timeouts from the
    // configuration, how often the account session is checked, and how long the client tries to
    // restore a lost connection. They never change afterwards.
    public sealed class ClientSettings
    {
        // Mirrors the 20 seconds that SimulationWorker keeps a character's body after its
        // connection ended. The server decides how long the body stays; this is only how long the
        // client tries before it gives up.
        public const int DefaultReconnectWindowSeconds = 20;

        // The environment of settings that come from no environment file, such as a test's.
        public const string UnnamedEnvironment = "unnamed";

        public ClientSettings(
            string gameServiceBaseUrl,
            int requestTimeoutSeconds,
            int realtimeTimeoutSeconds,
            int sessionValidationIntervalSeconds,
            int reconnectWindowSeconds = DefaultReconnectWindowSeconds)
            : this(
                new ClientBuildInfo(Unnamed(gameServiceBaseUrl), ClientBuildStamp.Editor),
                string.Empty,
                requestTimeoutSeconds,
                realtimeTimeoutSeconds,
                sessionValidationIntervalSeconds,
                reconnectWindowSeconds)
        {
        }

        public ClientSettings(
            ClientBuildInfo build,
            string gameServiceOverride,
            int requestTimeoutSeconds,
            int realtimeTimeoutSeconds,
            int sessionValidationIntervalSeconds,
            int reconnectWindowSeconds = DefaultReconnectWindowSeconds)
        {
            if (build == null)
            {
                throw new ArgumentNullException(nameof(build));
            }

            var overrideAddress = string.Empty;
            if (!string.IsNullOrEmpty(gameServiceOverride)
                && !ClientEnvironment.TryNormalizeAddress(gameServiceOverride, out overrideAddress))
            {
                throw new ArgumentException(
                    "A GameService override is an absolute http or https URL.",
                    nameof(gameServiceOverride));
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

            Environment = build.Environment;
            Build = build.Stamp;
            GameServiceOverride = overrideAddress;
            GameServiceBaseUrl = overrideAddress.Length > 0 ? overrideAddress : build.Environment.GameServiceBaseUrl;
            RequestTimeoutSeconds = requestTimeoutSeconds;
            RealtimeTimeoutSeconds = realtimeTimeoutSeconds;
            SessionValidationIntervalSeconds = sessionValidationIntervalSeconds;
            ReconnectWindowSeconds = reconnectWindowSeconds;
        }

        // The environment this client was built for, or the Editor's local environment.
        public ClientEnvironment Environment { get; }

        // Which build this is.
        public ClientBuildStamp Build { get; }

        // The address that replaced the environment's at start, empty without an override.
        public string GameServiceOverride { get; }

        public bool IsGameServiceOverridden
        {
            get { return GameServiceOverride.Length > 0; }
        }

        // Where every request goes: the override, or else the environment's address.
        public string GameServiceBaseUrl { get; }

        public int RequestTimeoutSeconds { get; }

        public int RealtimeTimeoutSeconds { get; }

        public int SessionValidationIntervalSeconds { get; }

        // How long after a lost connection the client tries to join the World again.
        public int ReconnectWindowSeconds { get; }

        public static ClientSettings From(ClientConfig config, ClientBuildInfo build, string gameServiceOverride)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return new ClientSettings(
                build,
                gameServiceOverride,
                config.RequestTimeoutSeconds,
                config.RealtimeTimeoutSeconds,
                config.SessionValidationIntervalSeconds,
                config.ReconnectWindowSeconds);
        }

        // The same settings with another GameService, such as an unreachable one in a test.
        public ClientSettings WithGameServiceBaseUrl(string gameServiceBaseUrl)
        {
            return new ClientSettings(
                new ClientBuildInfo(new ClientEnvironment(Environment.Name, gameServiceBaseUrl), Build),
                string.Empty,
                RequestTimeoutSeconds,
                RealtimeTimeoutSeconds,
                SessionValidationIntervalSeconds,
                ReconnectWindowSeconds);
        }

        private static ClientEnvironment Unnamed(string gameServiceBaseUrl)
        {
            if (!ClientEnvironment.TryNormalizeAddress(gameServiceBaseUrl, out _))
            {
                throw new ArgumentException("A GameService base URL is required.", nameof(gameServiceBaseUrl));
            }

            return new ClientEnvironment(UnnamedEnvironment, gameServiceBaseUrl);
        }
    }
}
