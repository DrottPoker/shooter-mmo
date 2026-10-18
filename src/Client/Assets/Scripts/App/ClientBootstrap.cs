using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.App
{
    // Composition root: owns the persistent client services and passes them explicitly to
    // every component that needs them. No runtime assembly references this one.
    public sealed class ClientBootstrap : MonoBehaviour
    {
        private string previousSceneName;
        private ApiClient apiClient;
        private Coroutine accountSessionMonitor;
        private AsyncOperationHandle<ClientConfig> configHandle;
        private RealtimeSimulationClient simulationClient;
        private InventoryClientController inventoryController;
        private CorpseClientController corpseController;
        private WorldActorClientController worldActorController;
        private WorldInteractionClientController worldInteractionController;
        private readonly WorldPointerFocus pointerFocus = new WorldPointerFocus();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
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
            var config = ClientConfig.Load(out configHandle);
            ClientSession.Configure(config);
            apiClient = new ApiClient(config.RequestTimeoutSeconds);
            simulationClient = GetOrAddComponent<RealtimeSimulationClient>();

            inventoryController = GetOrAddComponent<InventoryClientController>();
            inventoryController.Initialize(apiClient, simulationClient, config.ItemGameplayCatalog);

            corpseController = GetOrAddComponent<CorpseClientController>();
            corpseController.Initialize(simulationClient, inventoryController);

            worldActorController = GetOrAddComponent<WorldActorClientController>();
            worldActorController.Initialize(simulationClient);

            worldInteractionController = GetOrAddComponent<WorldInteractionClientController>();
            worldInteractionController.Initialize(
                simulationClient,
                worldActorController,
                corpseController,
                inventoryController);

            // Mirrors the worker's combat state across scenes for later combat presentation.
            GetOrAddComponent<CombatClientController>().Initialize(simulationClient);

            simulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            previousSceneName = SceneManager.GetActiveScene().name;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            ComposeScene(SceneManager.GetActiveScene());
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
            if (simulationClient != null)
            {
                simulationClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            }

            apiClient?.Dispose();
            apiClient = null;
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        private T GetOrAddComponent<T>() where T : Component
        {
            var component = GetComponent<T>();
            return component != null ? component : gameObject.AddComponent<T>();
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
            ComposeScene(scene);
        }

        private IEnumerator ReleaseAbandonedSimulationSessionRoutine()
        {
            var session = ClientSession.ActiveSimulationSession;
            if (session == null)
            {
                yield break;
            }

            RealtimeClientError error = null;
            if (simulationClient != null && simulationClient.IsJoined)
            {
                yield return simulationClient.Leave(
                    session.simulationSessionId,
                    () => { },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDisplayMessage());
                if (simulationClient != null)
                {
                    simulationClient.Abort();
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
                StringComparison.Ordinal))
            {
                ClientLog.Error(
                    ClientLogCategory.Auth,
                    "This client was disconnected because the account logged in from another client. "
                    + error.ToDisplayMessage());
                ClientSession.Clear();
                if (SceneManager.GetActiveScene().name != SceneNames.LoginMenu)
                {
                    ClientSceneLoader.LoadLoginMenu();
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
                if (ClientSession.IsAuthenticated)
                {
                    ClientSceneLoader.LoadCharacterSelect();
                }
                else
                {
                    ClientSceneLoader.LoadLoginMenu();
                }
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
                    StringComparison.Ordinal))
                {
                    continue;
                }

                ClientSessionRecovery.ReturnToLoginIfUnauthorized(error, simulationClient);
            }
        }

        // Scene-authored components receive their services when the scene loads, before
        // Start. Runtime scene controllers are created inactive, initialized, then activated,
        // so Awake and OnEnable already see their dependencies.
        private void ComposeScene(Scene scene)
        {
            if (scene.name == SceneNames.LoginMenu)
            {
                AddSceneComponentIfMissing<LoginMenuPanel>(null);
                return;
            }

            if (scene.name == SceneNames.CharacterSelect)
            {
                AddSceneComponentIfMissing<CharacterSelectPanel>(
                    panel => panel.Initialize(simulationClient));
                return;
            }

            if (!WorldSceneCatalog.IsWorldScene(scene.name))
            {
                return;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var context in root.GetComponentsInChildren<WorldSceneContext>(true))
                {
                    context.Initialize(simulationClient, pointerFocus);
                }
            }

            AddSceneComponentIfMissing<WorldScenePanel>(
                panel => panel.Initialize(simulationClient, pointerFocus));
            AddSceneComponentIfMissing<MovementDestinationMarker>(null);
            AddSceneComponentIfMissing<TemporaryInventoryPanel>(
                panel => panel.Initialize(
                    inventoryController,
                    corpseController,
                    worldInteractionController));
            AddSceneComponentIfMissing<CorpsePresentationController>(
                presentation => presentation.Initialize(corpseController));
            AddSceneComponentIfMissing<WorldActorPresentationController>(
                presentation => presentation.Initialize(worldActorController));
            AddSceneComponentIfMissing<WorldInteractionTargetingController>(
                targeting => targeting.Initialize(
                    worldInteractionController,
                    worldActorController));
            AddSceneComponentIfMissing<TemporaryWorldInteractionPanel>(
                panel => panel.Initialize(worldInteractionController, inventoryController));
        }

        private static void AddSceneComponentIfMissing<T>(Action<T> initialize)
            where T : Component
        {
            if (FindAnyObjectByType<T>() != null)
            {
                return;
            }

            var gameObject = new GameObject(typeof(T).Name);
            gameObject.SetActive(false);
            var component = gameObject.AddComponent<T>();
            initialize?.Invoke(component);
            gameObject.SetActive(true);
        }
    }
}
