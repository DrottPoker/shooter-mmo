using System.Collections.Generic;
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

        // How long the client tries to restore a lost connection before it returns to character
        // select. It mirrors the 20 seconds that the worker keeps the character's body; the server
        // decides how long the body stays.
        [SerializeField] private int reconnectWindowSeconds = ClientSettings.DefaultReconnectWindowSeconds;

        // The bundled content set: the seed and fallback of the content that the client resolves
        // from GameService at start, as ClientContentResolver describes.
        // The compiled item catalog, core.item-catalog.json.
        [SerializeField] private TextAsset itemGameplayCatalog;

        // The compiled quest catalog, core.quest-catalog.json, whose rules the quest windows read.
        [SerializeField] private TextAsset questCatalog;

        // The text tables of the client's locale, such as items.text-table.json, which hold
        // every content text that the client shows.
        [SerializeField] private TextAsset[] textTables = new TextAsset[0];

        // The material of primitives that runtime presentation creates itself. Referencing it
        // here puts it and its URP shader into the client content.
        [SerializeField] private Material runtimePrimitiveMaterial;

        // The unlit material of the Debug menu's lines, such as the collision overlay, which sets
        // its color per draw.
        [SerializeField] private Material debugLineMaterial;

        // The arrow that flies for every weapon that shoots arrows and lies on an archer's string,
        // the model of art/Projectiles/BronzeArrow, with its point at its root.
        [SerializeField] private GameObject arrowModel;

        // The particle materials of the wakes that players and Mobs leave in water: a ring that
        // spreads on the surface and a droplet of spray.
        [SerializeField] private Material waterRingMaterial;
        [SerializeField] private Material waterSprayMaterial;

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

        public int ReconnectWindowSeconds
        {
            get { return Mathf.Clamp(reconnectWindowSeconds, 1, 120); }
        }

        public TextAsset ItemGameplayCatalog
        {
            get { return itemGameplayCatalog; }
        }

        public TextAsset QuestCatalog
        {
            get { return questCatalog; }
        }

        public IReadOnlyList<TextAsset> TextTables
        {
            get { return textTables; }
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

        public Material WaterRingMaterial
        {
            get { return waterRingMaterial; }
        }

        public Material WaterSprayMaterial
        {
            get { return waterSprayMaterial; }
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
