using UnityEngine;

namespace Mmorpg.Client.Config
{
    [CreateAssetMenu(fileName = "ClientConfig", menuName = "MMORPG/Client Config")]
    public sealed class ClientConfig : ScriptableObject
    {
        [SerializeField] private string gameServiceBaseUrl = "http://localhost:5000";
        [SerializeField] private int requestTimeoutSeconds = 10;
        [SerializeField] private int realtimeTimeoutSeconds = 10;
        [SerializeField] private int sessionValidationIntervalSeconds = 5;
        [SerializeField] private TextAsset itemGameplayCatalog;

        public string GameServiceBaseUrl
        {
            get { return NormalizeUrl(gameServiceBaseUrl, "http://localhost:5000"); }
        }

        public int RequestTimeoutSeconds
        {
            get { return Mathf.Clamp(requestTimeoutSeconds, 1, 120); }
        }

        public int RealtimeTimeoutSeconds
        {
            get { return Mathf.Clamp(realtimeTimeoutSeconds, 1, 120); }
        }

        public int SessionValidationIntervalSeconds
        {
            get { return Mathf.Clamp(sessionValidationIntervalSeconds, 2, 60); }
        }

        public TextAsset ItemGameplayCatalog
        {
            get { return itemGameplayCatalog; }
        }

        public static ClientConfig Load()
        {
            var config = Resources.Load<ClientConfig>("Config/ClientConfig");
            if (config != null)
            {
                return config;
            }

            Debug.LogWarning(
                "Resources/Config/ClientConfig is missing. Using local fallback endpoints.");
            return CreateInstance<ClientConfig>();
        }

        private static string NormalizeUrl(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');
        }
    }
}
