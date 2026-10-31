using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Core
{
    [CreateAssetMenu(fileName = "ClientConfig", menuName = "MMORPG/Client Config")]
    public sealed class ClientConfig : ScriptableObject
    {
        [SerializeField] private string gameServiceBaseUrl = "http://localhost:5000";
        [SerializeField] private int requestTimeoutSeconds = 10;
        [SerializeField] private int realtimeTimeoutSeconds = 10;
        [SerializeField] private int sessionValidationIntervalSeconds = 5;
        [SerializeField] private TextAsset itemGameplayCatalog;

        // The material of primitives that runtime presentation creates itself. Referencing it
        // here puts it and its URP shader into the client content.
        [SerializeField] private Material runtimePrimitiveMaterial;

        // The unlit material of the Debug menu's lines, such as the collision overlay, which sets
        // its color per draw.
        [SerializeField] private Material debugLineMaterial;

        // The arrow that flies for every weapon that shoots arrows and lies on an archer's string,
        // the model of art/Projectiles/BronzeArrow, with its point at its root.
        [SerializeField] private GameObject arrowModel;

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

        public Material RuntimePrimitiveMaterial
        {
            get { return runtimePrimitiveMaterial; }
        }

        public Material DebugLineMaterial
        {
            get { return debugLineMaterial; }
        }

        public GameObject ArrowModel
        {
            get { return arrowModel; }
        }

        // The handle keeps the configuration and the catalog it references loaded. The owner
        // releases it with ClientAssets.Release; the fallback instance has no handle.
        public static ClientConfig Load(out AsyncOperationHandle<ClientConfig> handle)
        {
            if (ClientAssets.TryLoadNow(ClientAddresses.ClientConfig, out handle, out var error))
            {
                return handle.Result;
            }

            Debug.LogWarning(
                "The client configuration is unavailable: " + error + " Using local fallback endpoints.");
            return CreateInstance<ClientConfig>();
        }

        private static string NormalizeUrl(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');
        }
    }
}
