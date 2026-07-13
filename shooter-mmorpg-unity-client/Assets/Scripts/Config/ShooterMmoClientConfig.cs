using UnityEngine;

namespace ShooterMmo.Config
{
    [CreateAssetMenu(fileName = "ShooterMmoClientConfig", menuName = "Shooter MMO/Client Config")]
    public sealed class ShooterMmoClientConfig : ScriptableObject
    {
        [SerializeField] private string authServiceBaseUrl = "http://localhost:5000";
        [SerializeField] private int requestTimeoutSeconds = 10;
        [SerializeField] private int realtimeTimeoutSeconds = 10;
        [SerializeField] private int sessionValidationIntervalSeconds = 5;

        public string AuthServiceBaseUrl
        {
            get { return NormalizeUrl(authServiceBaseUrl, "http://localhost:5000"); }
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

        public static ShooterMmoClientConfig Load()
        {
            var config = Resources.Load<ShooterMmoClientConfig>("Config/ShooterMmoClientConfig");
            if (config != null)
            {
                return config;
            }

            Debug.LogWarning(
                "Resources/Config/ShooterMmoClientConfig is missing. Using local fallback endpoints.");
            return CreateInstance<ShooterMmoClientConfig>();
        }

        private static string NormalizeUrl(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');
        }
    }
}
