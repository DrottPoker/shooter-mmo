using System;
using System.Collections;
using System.Linq;
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
        private CombatClientController combatController;
        private Material runtimePrimitiveMaterial;
        private readonly WorldPointerFocus pointerFocus = new WorldPointerFocus();
        private readonly GameMessageLog gameMessages = new GameMessageLog();

        // The World Debug panel's body tint, kept between sessions and off by default.
        private const string BodyTintPreference = "Mmorpg.Debug.TintPlayerBodies";

        // Where the panel kept a look and body model before the server stored them; removed at start.
        private static readonly string[] RetiredPreferences =
        {
            "Mmorpg.Debug.PlayerBodyLook",
            "Mmorpg.Debug.PlayerBodyModel"
        };

        private PlayerBodyTintSetting bodyTint;

        // The local player's look as the server stores it, kept in step by the look link.
        private readonly PlayerBodyLookSetting bodyLook = new PlayerBodyLookSetting();
        private PlayerLookLink lookLink;

        // The items the local player wears, from the inventory as it shows them.
        private readonly PlayerEquipment playerEquipment = new PlayerEquipment();

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
            runtimePrimitiveMaterial = config.RuntimePrimitiveMaterial;
            if (runtimePrimitiveMaterial == null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client configuration has no runtime primitive material, so markers, arrows, and fallback models render with the error shader in a player build.");
            }
            apiClient = new ApiClient(config.RequestTimeoutSeconds);
            bodyTint = new PlayerBodyTintSetting(PlayerPrefs.GetInt(BodyTintPreference, 0) == 1);
            bodyTint.Changed += SaveBodyTint;
            DeleteRetiredPreferences();
            simulationClient = GetOrAddComponent<RealtimeSimulationClient>();
            lookLink = new PlayerLookLink(simulationClient, bodyLook, gameMessages);

            inventoryController = GetOrAddComponent<InventoryClientController>();
            inventoryController.Initialize(apiClient, simulationClient, config.ItemGameplayCatalog);
            inventoryController.State.Changed += UpdatePlayerEquipment;
            UpdatePlayerEquipment();

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

            // Mirrors the worker's combat state across scenes.
            combatController = GetOrAddComponent<CombatClientController>();
            combatController.Initialize(simulationClient);

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

            if (bodyTint != null)
            {
                bodyTint.Changed -= SaveBodyTint;
            }

            lookLink?.Dispose();
            lookLink = null;

            if (inventoryController != null)
            {
                inventoryController.State.Changed -= UpdatePlayerEquipment;
            }

            apiClient?.Dispose();
            apiClient = null;
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        private void SaveBodyTint()
        {
            PlayerPrefs.SetInt(BodyTintPreference, bodyTint.Enabled ? 1 : 0);
            PlayerPrefs.Save();
        }

        private static void DeleteRetiredPreferences()
        {
            foreach (var key in RetiredPreferences)
            {
                PlayerPrefs.DeleteKey(key);
            }
        }

        // The equipped items of the shown inventory, with its predicted moves, so the local body
        // changes its garments and attachments as soon as an item moves.
        private void UpdatePlayerEquipment()
        {
            var state = inventoryController.State;
            var snapshot = state.FullSnapshot;
            playerEquipment.Set(
                snapshot == null
                    ? null
                    : snapshot.Equipment
                        .Where(slot => slot.Item != null)
                        .OrderBy(slot => slot.SortOrder)
                        .Select(slot => new EquippedItem(slot.SlotId, slot.Item.DefinitionId))
                        .ToArray(),
                state.Catalog != null ? state.Catalog.Presentation : null);
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

            WorldSceneContext sceneContext = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var context in root.GetComponentsInChildren<WorldSceneContext>(true))
                {
                    context.Initialize(simulationClient, pointerFocus, bodyTint, bodyLook, playerEquipment);
                    sceneContext = sceneContext != null ? sceneContext : context;
                }
            }

            AddSceneComponentIfMissing<WorldScenePanel>(
                panel => panel.Initialize(simulationClient, pointerFocus, bodyTint, bodyLook));
            AddSceneComponentIfMissing<MovementDestinationMarker>(
                marker => marker.Initialize(runtimePrimitiveMaterial));
            var worldMenu = AddSceneComponentIfMissing<WorldMenuController>(
                menu => menu.Initialize(worldActorController, gameMessages));
            AddSceneComponentIfMissing<TemporaryInventoryPanel>(
                panel => panel.Initialize(
                    inventoryController,
                    corpseController,
                    worldInteractionController,
                    combatController,
                    worldMenu,
                    bodyLook));
            AddSceneComponentIfMissing<CorpsePresentationController>(
                presentation => presentation.Initialize(corpseController, runtimePrimitiveMaterial, sceneContext));
            var actorPresentation = AddSceneComponentIfMissing<WorldActorPresentationController>(
                presentation => presentation.Initialize(
                    worldActorController,
                    simulationClient,
                    runtimePrimitiveMaterial));
            AddSceneComponentIfMissing<WorldInteractionTargetingController>(
                targeting => targeting.Initialize(
                    worldInteractionController,
                    worldActorController,
                    worldMenu,
                    gameMessages));
            AddSceneComponentIfMissing<TemporaryWorldInteractionPanel>(
                panel => panel.Initialize(worldInteractionController, inventoryController));
            AddSceneComponentIfMissing<TemporaryWorldContextMenu>(
                menu => menu.Initialize(worldMenu));
            AddSceneComponentIfMissing<TemporaryGameMessagePanel>(
                panel => panel.Initialize(gameMessages));

            var combatantViews = new CombatantViewLocator(
                simulationClient,
                sceneContext,
                actorPresentation);
            var combatTargeting = AddSceneComponentIfMissing<CombatTargetingController>(
                targeting => targeting.Initialize(
                    combatController,
                    simulationClient,
                    worldActorController,
                    inventoryController,
                    combatantViews,
                    worldMenu));
            AddSceneComponentIfMissing<CombatProjectilePresentation>(
                presentation => presentation.Initialize(
                    combatController,
                    simulationClient,
                    combatantViews,
                    runtimePrimitiveMaterial));
            AddSceneComponentIfMissing<CombatDeathPresentation>(
                presentation => presentation.Initialize(combatController, combatantViews));
            AddSceneComponentIfMissing<TemporaryCombatHud>(
                hud => hud.Initialize(
                    combatController,
                    combatTargeting,
                    simulationClient,
                    combatantViews));
        }

        // Returns the scene's existing component, or a new one initialized before it activates.
        private static T AddSceneComponentIfMissing<T>(Action<T> initialize)
            where T : Component
        {
            var existing = FindAnyObjectByType<T>();
            if (existing != null)
            {
                return existing;
            }

            var gameObject = new GameObject(typeof(T).Name);
            gameObject.SetActive(false);
            var component = gameObject.AddComponent<T>();
            initialize?.Invoke(component);
            gameObject.SetActive(true);
            return component;
        }
    }
}
