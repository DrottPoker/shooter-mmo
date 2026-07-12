using UnityEngine;

namespace ShooterMmo.Config
{
    [CreateAssetMenu(fileName = "ShooterMmoClientConfig", menuName = "Shooter MMO/Client Config")]
    public sealed class ShooterMmoClientConfig : ScriptableObject
    {
        [SerializeField] private string authServiceBaseUrl = "http://localhost:5000";
        [SerializeField] private string worldServerBaseUrl = "http://localhost:5100";
        [SerializeField] private int requestTimeoutSeconds = 10;

        public string AuthServiceBaseUrl
        {
            get { return NormalizeUrl(authServiceBaseUrl, "http://localhost:5000"); }
        }

        public string WorldServerBaseUrl
        {
            get { return NormalizeUrl(worldServerBaseUrl, "http://localhost:5100"); }
        }

        public int RequestTimeoutSeconds
        {
            get { return Mathf.Clamp(requestTimeoutSeconds, 1, 120); }
        }

        public static ShooterMmoClientConfig Load()
        {
            var config = Resources.Load<ShooterMmoClientConfig>("ShooterMmoClientConfig");
            if (config != null)
            {
                return config;
            }

            Debug.LogWarning("Resources/ShooterMmoClientConfig is missing. Using local fallback endpoints.");
            return CreateInstance<ShooterMmoClientConfig>();
        }

        private static string NormalizeUrl(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');
        }
    }
}
