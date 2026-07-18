using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.Diagnostics;
using ShooterMmo.Items;
using ShooterMmo.Networking;
using ShooterMmo.WorldActors;
using ShooterMmo.Worlds;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo
{
    public sealed class ShooterMmoClientBootstrap : MonoBehaviour
    {
        private string previousSceneName;
        private ShooterMmoApiClient apiClient;
        private Coroutine accountSessionMonitor;

        public static RealtimeSimulationClient SimulationClient { get; private set; }

        public static InventoryClientController InventoryController { get; private set; }

        public static CorpseClientController CorpseController { get; private set; }

        public static WorldActorClientController WorldActorController { get; private set; }

        public static WorldInteractionClientController WorldInteractionController { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ShooterMmoClientSession.Configure(ShooterMmoClientConfig.Load());

            if (FindAnyObjectByType<ShooterMmoClientBootstrap>() != null)
            {
                return;
            }

            var gameObject = new GameObject("ShooterMmoClientBootstrap");
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<ShooterMmoClientBootstrap>();
        }

        private void Awake()
        {
            var config = ShooterMmoClientConfig.Load();
            apiClient = new ShooterMmoApiClient(config.RequestTimeoutSeconds);
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
                && ShooterMmoClientSession.ActiveSimulationSession != null)
            {
                StartCoroutine(ReleaseAbandonedSimulationSessionRoutine());
            }

            previousSceneName = scene.name;
            EnsureSceneController(scene.name);
        }

        private IEnumerator ReleaseAbandonedSimulationSessionRoutine()
        {
            var session = ShooterMmoClientSession.ActiveSimulationSession;
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

            if (ShooterMmoClientSession.ActiveSimulationSession != null
                && ShooterMmoClientSession.ActiveSimulationSession.simulationSessionId == session.simulationSessionId)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
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
                ShooterMmoClientSession.Clear();
                if (SceneManager.GetActiveScene().name != ShooterMmoSceneNames.LoginMenu)
                {
                    SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
                }

                return;
            }

            ClientLog.Warning(
                ClientLogCategory.Client,
                "The active simulation connection closed. Clearing local shard state and leaving the world scene: "
                + error.ToDisplayMessage());
            ShooterMmoClientSession.ActiveSimulationSession = null;

            if (WorldSceneCatalog.IsWorldScene(SceneManager.GetActiveScene().name))
            {
                SceneManager.LoadScene(ShooterMmoClientSession.IsAuthenticated
                    ? ShooterMmoSceneNames.CharacterSelect
                    : ShooterMmoSceneNames.LoginMenu);
            }
        }

        private IEnumerator MonitorAccountSessionRoutine()
        {
            var wait = new WaitForSecondsRealtime(
                ShooterMmoClientSession.SessionValidationIntervalSeconds);
            while (true)
            {
                yield return wait;

                var token = ShooterMmoClientSession.SessionToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                ShooterMmoApiError error = null;
                yield return apiClient.ValidateSession(
                    ShooterMmoClientSession.AuthServiceBaseUrl,
                    token,
                    () => { },
                    value => error = value);

                if (!string.Equals(
                    token,
                    ShooterMmoClientSession.SessionToken,
                    System.StringComparison.Ordinal))
                {
                    continue;
                }

                ClientSessionRecovery.ReturnToLoginIfUnauthorized(error);
            }
        }

        private static void EnsureSceneController(string sceneName)
        {
            if (sceneName == ShooterMmoSceneNames.LoginMenu)
            {
                AddControllerIfMissing<Ui.LoginMenuPanel>();
                return;
            }

            if (sceneName == ShooterMmoSceneNames.CharacterSelect)
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
