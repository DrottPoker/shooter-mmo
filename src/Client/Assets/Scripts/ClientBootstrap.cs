using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Config;
using Mmorpg.Client.Diagnostics;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client
{
    public sealed class ClientBootstrap : MonoBehaviour
    {
        private string previousSceneName;
        private ApiClient apiClient;
        private Coroutine accountSessionMonitor;

        public static RealtimeSimulationClient SimulationClient { get; private set; }

        public static InventoryClientController InventoryController { get; private set; }

        public static CorpseClientController CorpseController { get; private set; }

        public static WorldActorClientController WorldActorController { get; private set; }

        public static WorldInteractionClientController WorldInteractionController { get; private set; }

        // Static state survives entering Play Mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            SimulationClient = null;
            InventoryController = null;
            CorpseController = null;
            WorldActorController = null;
            WorldInteractionController = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ClientSession.Configure(ClientConfig.Load());

            if (FindAnyObjectByType<ClientBootstrap>() != null)
            {
                return;
            }

            var gameObject = new GameObject("ClientBootstrap");
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<ClientBootstrap>();
        }

        private void Awake()
        {
            var config = ClientConfig.Load();
            apiClient = new ApiClient(config.RequestTimeoutSeconds);
            SimulationClient = GetComponent<RealtimeSimulationClient>();
            if (SimulationClient == null)
            {
                SimulationClient = gameObject.AddComponent<RealtimeSimulationClient>();
            }

            InventoryController = GetComponent<InventoryClientController>();
            if (InventoryController == null)
            {
                InventoryController = gameObject.AddComponent<InventoryClientController>();
            }

            InventoryController.Initialize(apiClient, SimulationClient, config.ItemGameplayCatalog);

            CorpseController = GetComponent<CorpseClientController>();
            if (CorpseController == null)
            {
                CorpseController = gameObject.AddComponent<CorpseClientController>();
            }

            CorpseController.Initialize(SimulationClient, InventoryController);

            WorldActorController = GetComponent<WorldActorClientController>();
            if (WorldActorController == null)
            {
                WorldActorController = gameObject.AddComponent<WorldActorClientController>();
            }

            WorldActorController.Initialize(SimulationClient);

            WorldInteractionController = GetComponent<WorldInteractionClientController>();
            if (WorldInteractionController == null)
            {
                WorldInteractionController = gameObject.AddComponent<WorldInteractionClientController>();
            }

            WorldInteractionController.Initialize(
                SimulationClient,
                WorldActorController,
                CorpseController);

            SimulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            previousSceneName = SceneManager.GetActiveScene().name;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureSceneController(SceneManager.GetActiveScene().name);
            accountSessionMonitor = StartCoroutine(MonitorAccountSessionRoutine());
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (accountSessionMonitor != null)
            {
                StopCoroutine(accountSessionMonitor);
                accountSessionMonitor = null;
            }
        }

        private void OnDestroy()
        {
            if (SimulationClient != null)
            {
                SimulationClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            }

            apiClient?.Dispose();
            apiClient = null;

            SimulationClient = null;
            InventoryController = null;
            CorpseController = null;
            WorldActorController = null;
            WorldInteractionController = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (WorldSceneCatalog.IsWorldScene(previousSceneName)
                && !WorldSceneCatalog.IsWorldScene(scene.name)
                && ClientSession.ActiveSimulationSession != null)
            {
                StartCoroutine(ReleaseAbandonedSimulationSessionRoutine());
            }

            previousSceneName = scene.name;
            EnsureSceneController(scene.name);
        }

        private IEnumerator ReleaseAbandonedSimulationSessionRoutine()
        {
            var session = ClientSession.ActiveSimulationSession;
            if (session == null)
            {
                yield break;
            }

            RealtimeClientError error = null;
            if (SimulationClient != null && SimulationClient.IsJoined)
            {
                yield return SimulationClient.Leave(
                    session.simulationSessionId,
                    () => { },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDisplayMessage());
                if (SimulationClient != null)
                {
                    SimulationClient.Abort();
                }
            }

            if (ClientSession.ActiveSimulationSession != null
                && ClientSession.ActiveSimulationSession.simulationSessionId == session.simulationSessionId)
            {
                ClientSession.ActiveSimulationSession = null;
            }
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            if (string.Equals(
                error.Code,
                ClientSessionRecovery.AccountSessionReplacedCode,
                System.StringComparison.Ordinal))
            {
                ClientLog.Error(
                    ClientLogCategory.Auth,
                    "This client was disconnected because the account logged in from another client. "
                    + error.ToDisplayMessage());
                ClientSession.Clear();
                if (SceneManager.GetActiveScene().name != SceneNames.LoginMenu)
                {
                    SceneManager.LoadScene(SceneNames.LoginMenu);
                }

                return;
            }

            ClientLog.Warning(
                ClientLogCategory.Client,
                "The active simulation connection closed. Clearing local shard state and leaving the world scene: "
                + error.ToDisplayMessage());
            ClientSession.ActiveSimulationSession = null;

            if (WorldSceneCatalog.IsWorldScene(SceneManager.GetActiveScene().name))
            {
                SceneManager.LoadScene(ClientSession.IsAuthenticated
                    ? SceneNames.CharacterSelect
                    : SceneNames.LoginMenu);
            }
        }

        private IEnumerator MonitorAccountSessionRoutine()
        {
            var wait = new WaitForSecondsRealtime(
                ClientSession.SessionValidationIntervalSeconds);
            while (true)
            {
                yield return wait;

                var token = ClientSession.SessionToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                ApiError error = null;
                yield return apiClient.ValidateSession(
                    ClientSession.GameServiceBaseUrl,
                    token,
                    () => { },
                    value => error = value);

                if (!string.Equals(
                    token,
                    ClientSession.SessionToken,
                    System.StringComparison.Ordinal))
                {
                    continue;
                }

                ClientSessionRecovery.ReturnToLoginIfUnauthorized(error);
            }
        }

        private static void EnsureSceneController(string sceneName)
        {
            if (sceneName == SceneNames.LoginMenu)
            {
                AddControllerIfMissing<Ui.LoginMenuPanel>();
                return;
            }

            if (sceneName == SceneNames.CharacterSelect)
            {
                AddControllerIfMissing<Ui.CharacterSelectPanel>();
                return;
            }

            if (WorldSceneCatalog.IsWorldScene(sceneName))
            {
                AddControllerIfMissing<Gameplay.CrosshairController>();
                AddControllerIfMissing<Ui.WorldScenePanel>();
                AddControllerIfMissing<Ui.CrosshairPanel>();
                AddControllerIfMissing<Ui.TemporaryInventoryPanel>();
                AddControllerIfMissing<Gameplay.CorpsePresentationController>();
                AddControllerIfMissing<WorldActors.WorldActorPresentationController>();
                AddControllerIfMissing<WorldActors.WorldInteractionTargetingController>();
                AddControllerIfMissing<Ui.TemporaryWorldInteractionPanel>();
            }
        }

        private static void AddControllerIfMissing<T>() where T : Component
        {
            if (FindAnyObjectByType<T>() != null)
            {
                return;
            }

            var gameObject = new GameObject(typeof(T).Name);
            gameObject.AddComponent<T>();
        }
    }
}
