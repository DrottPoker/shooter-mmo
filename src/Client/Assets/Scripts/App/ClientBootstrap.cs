using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Quests;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Text;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.App
{
    // Composition root: owns the persistent client services and passes them explicitly to
    // every component that needs them. No runtime assembly references this one. Awake creates
    // what does not depend on content; Start resolves the content set, then creates the
    // controllers and composes the active scene. A scene that loads before that is composed
    // when the content is ready.
    public sealed class ClientBootstrap : MonoBehaviour
    {
        // Below Application.persistentDataPath.
        private const string ContentCacheFolder = "content";

        private string previousSceneName;
        private ApiClient apiClient;
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private Coroutine accountSessionMonitor;
        private AsyncOperationHandle<ClientConfig> configHandle;
        private ClientConfig config;
        private UnityWebContentTransport contentTransport;
        private ClientContentReport contentReport;
        private ContentCheckPanel contentCheckPanel;
        private RealtimeSimulationClient simulationClient;
        private InventoryClientController inventoryController;
        private QuestClientController questController;
        private CorpseClientController corpseController;
        private WorldActorClientController worldActorController;
        private WorldInteractionClientController worldInteractionController;
        private CombatClientController combatController;
        private Material runtimePrimitiveMaterial;
        private Material debugLineMaterial;
        private GameObject arrowModel;
        private Material waterRingMaterial;
        private Material waterSprayMaterial;
        private readonly WorldPointerFocus pointerFocus = new WorldPointerFocus();
        private readonly GameMessageLog gameMessages = new GameMessageLog();
        private readonly InventoryWindowRequests inventoryWindowRequests = new InventoryWindowRequests();
        private UiTheme uiTheme;
        private AsyncOperationHandle<UiTheme> uiThemeHandle;

        // The Debug menu's body tint, kept between sessions and off by default.
        private const string BodyTintPreference = "Mmorpg.Debug.TintPlayerBodies";

        // Where the Debug menu kept a look and body model before the server stored them; removed at start.
        private static readonly string[] RetiredPreferences =
        {
            "Mmorpg.Debug.PlayerBodyLook",
            "Mmorpg.Debug.PlayerBodyModel"
        };

        private PlayerBodyTintSetting bodyTint;

        // What the Debug menu draws in the world, off whenever the client starts.
        private readonly WorldDebugViewSettings debugView = new WorldDebugViewSettings();

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

        // True once the content is resolved and the controllers exist.
        public bool IsContentReady { get; private set; }

        public ClientContentReport ContentReport
        {
            get { return contentReport; }
        }

        private void Awake()
        {
            config = ClientConfig.Load(out configHandle);
            ClientSession.Configure(config);
            runtimePrimitiveMaterial = config.RuntimePrimitiveMaterial;
            debugLineMaterial = config.DebugLineMaterial;
            if (runtimePrimitiveMaterial == null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client configuration has no runtime primitive material, so markers, arrows, and fallback models render with the error shader in a player build.");
            }

            if (debugLineMaterial == null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client configuration has no debug line material, so the Debug menu draws no collision or path.");
            }

            arrowModel = config.ArrowModel;
            if (arrowModel == null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client configuration has no arrow model, so arrows fly as plain bars.");
            }

            waterRingMaterial = config.WaterRingMaterial;
            waterSprayMaterial = config.WaterSprayMaterial;
            if (waterRingMaterial == null || waterSprayMaterial == null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client configuration lacks a water wake material, so characters leave no rings or spray in water.");
            }

            uiTheme = UiTheme.Load(out uiThemeHandle);
            apiClient = new ApiClient(config.RequestTimeoutSeconds);
            bodyTint = new PlayerBodyTintSetting(PlayerPrefs.GetInt(BodyTintPreference, 0) == 1);
            bodyTint.Changed += SaveBodyTint;
            DeleteRetiredPreferences();
            simulationClient = GetOrAddComponent<RealtimeSimulationClient>();
            simulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            previousSceneName = SceneManager.GetActiveScene().name;
        }

        private void Start()
        {
            StartCoroutine(ResolveContentRoutine());
        }

        // The status line shows while the check runs; no controller or panel that needs content
        // exists before it ends.
        private IEnumerator ResolveContentRoutine()
        {
            var requests = BundledContentRequests(config);
            ShowContentCheck(requests);

            contentTransport = new UnityWebContentTransport();
            var resolver = new ClientContentResolver(
                ClientSession.GameServiceBaseUrl,
                contentTransport,
                new ClientContentCache(Path.Combine(Application.persistentDataPath, ContentCacheFolder)),
                requests,
                Debug.isDebugBuild && PlayerPrefs.GetInt(ClientContentResolver.IgnoreBundledContentPreference, 0) == 1);
            ClientContentReport report = null;
            yield return resolver.Resolve(value => report = value);
            LogContent(report);
            contentReport = report;

            if (contentCheckPanel != null)
            {
                Destroy(contentCheckPanel.gameObject);
                contentCheckPanel = null;
            }

            CreateControllers(report);
            IsContentReady = true;
            ComposeScene(SceneManager.GetActiveScene());
        }

        private static IReadOnlyList<ClientContentRequest> BundledContentRequests(ClientConfig config)
        {
            var requests = new List<ClientContentRequest>
            {
                ClientItemCatalog.ContentRequest(config.ItemGameplayCatalog != null ? config.ItemGameplayCatalog.text : null),
                ClientQuestCatalog.ContentRequest(config.QuestCatalog != null ? config.QuestCatalog.text : null)
            };
            requests.AddRange(ClientTexts.ContentRequests(
                (config.TextTables ?? Array.Empty<TextAsset>()).Select(asset => asset != null ? asset.text : null).ToArray()));
            return requests;
        }

        // The status line comes from the bundled tables, which are there before the check.
        private void ShowContentCheck(IReadOnlyList<ClientContentRequest> requests)
        {
            var bundledTexts = LoadTexts(ClientContentReport.Bundled(requests, null, null, null));
            var panelObject = new GameObject(nameof(ContentCheckPanel));
            panelObject.SetActive(false);
            contentCheckPanel = panelObject.AddComponent<ContentCheckPanel>();
            contentCheckPanel.Initialize(bundledTexts.Get("ui/login/checking-content"));
            panelObject.SetActive(true);
        }

        private static void LogContent(ClientContentReport report)
        {
            foreach (var file in report.Files)
            {
                ClientLog.Info(
                    ClientLogCategory.Client,
                    "Content " + file.Kind + " " + (file.Locale.Length > 0 ? file.Locale + "/" : string.Empty) + file.Id
                    + " revision " + file.Revision + " from " + file.Source + ".");
            }

            foreach (var warning in report.Warnings)
            {
                ClientLog.Warning(ClientLogCategory.Client, warning);
            }

            if (report.FellBackToBundled)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "The client runs with its bundled content [" + report.FallbackCode + "]: " + report.FallbackDiagnostic);
            }
        }

        private void CreateControllers(ClientContentReport report)
        {
            texts = LoadTexts(report);
            lookLink = new PlayerLookLink(simulationClient, bodyLook, gameMessages, texts);

            inventoryController = GetOrAddComponent<InventoryClientController>();
            inventoryController.Initialize(
                apiClient,
                simulationClient,
                report.TextOf(ClientContentKinds.ItemCatalog, ClientContentKinds.CoreCatalogId),
                texts);
            inventoryController.State.Changed += UpdatePlayerEquipment;
            UpdatePlayerEquipment();

            questController = GetOrAddComponent<QuestClientController>();
            questController.Initialize(
                apiClient,
                simulationClient,
                report.TextOf(ClientContentKinds.QuestCatalog, ClientContentKinds.CoreCatalogId),
                texts,
                inventoryController);

            corpseController = GetOrAddComponent<CorpseClientController>();
            corpseController.Initialize(simulationClient, inventoryController, texts);

            worldActorController = GetOrAddComponent<WorldActorClientController>();
            worldActorController.Initialize(simulationClient, texts);

            worldInteractionController = GetOrAddComponent<WorldInteractionClientController>();
            worldInteractionController.Initialize(
                simulationClient,
                worldActorController,
                corpseController,
                inventoryController,
                texts);
            worldInteractionController.Gathering.Changed += UpdateUsedEquipment;
            UpdateUsedEquipment();

            // Mirrors the worker's combat state across scenes.
            combatController = GetOrAddComponent<CombatClientController>();
            combatController.Initialize(simulationClient);
        }

        // Without readable text tables the client still runs, and every content text shows as
        // its key in brackets.
        private static TextCatalog LoadTexts(ClientContentReport report)
        {
            if (ClientTexts.TryLoad(report.TextsOf(ClientContentKinds.TextTable), out var texts, out var error))
            {
                // The pseudo-locale alters every text of the tables, so a text that still
                // reads as plain English on screen is a literal in the code.
                return PlayerPrefs.GetInt(ClientTexts.PseudoLocalePreference, 0) == 1
                    ? texts.ToPseudoLocale()
                    : texts;
            }

            ClientLog.Error(
                ClientLogCategory.Client,
                "The text tables are unavailable, so content text shows as keys: " + error);
            return TextCatalog.Empty(TextTableFormat.SourceLocale);
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
            contentTransport?.Dispose();
            contentTransport = null;

            if (inventoryController != null)
            {
                inventoryController.State.Changed -= UpdatePlayerEquipment;
            }

            if (worldInteractionController != null)
            {
                worldInteractionController.Gathering.Changed -= UpdateUsedEquipment;
            }

            apiClient?.Dispose();
            apiClient = null;
            config = null;
            ClientAssets.Release(configHandle);
            configHandle = default;
            ClientAssets.Release(uiThemeHandle);
            uiThemeHandle = default;
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

        // The local body holds the tool of the Tool slot while the character gathers.
        private void UpdateUsedEquipment()
        {
            playerEquipment.Use(worldInteractionController.Gathering.UsedEquipmentSlot);
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
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDiagnostic());
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
                    + error.ToDiagnostic());
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
                + error.ToDiagnostic());
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
            if (!IsContentReady)
            {
                return;
            }

            if (scene.name == SceneNames.LoginMenu)
            {
                AddSceneComponentIfMissing<LoginMenuPanel>(panel => panel.Initialize(texts, ClientNotice()));
                return;
            }

            if (scene.name == SceneNames.CharacterSelect)
            {
                AddSceneComponentIfMissing<CharacterSelectPanel>(
                    panel => panel.Initialize(simulationClient, texts));
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

            AddSceneComponentIfMissing<TemporaryDebugMenu>(
                menu => menu.Initialize(simulationClient, bodyTint, debugView, debugLineMaterial, texts, contentReport));
            AddSceneComponentIfMissing<MovementDestinationMarker>(
                marker => marker.Initialize(runtimePrimitiveMaterial));
            var worldMenu = AddSceneComponentIfMissing<WorldMenuController>(
                menu => menu.Initialize(worldActorController, gameMessages, texts));
            AddSceneComponentIfMissing<TemporaryAdminMenu>(
                menu => menu.Initialize(
                    simulationClient,
                    bodyLook,
                    worldMenu,
                    combatController,
                    inventoryWindowRequests,
                    questController));
            var inventoryWindows = AddSceneComponentIfMissing<InventoryWindows>(
                windows => windows.Initialize(
                    uiTheme,
                    inventoryController,
                    corpseController,
                    worldInteractionController,
                    combatController,
                    worldMenu,
                    gameMessages,
                    inventoryWindowRequests,
                    texts,
                    bodyLook,
                    sceneContext != null ? sceneContext.Preview : null,
                    pointerFocus));
            AddSceneComponentIfMissing<QuestWindows>(
                windows => windows.Initialize(
                    uiTheme,
                    questController,
                    inventoryController,
                    worldInteractionController,
                    gameMessages,
                    texts,
                    worldMenu,
                    inventoryWindows));
            var actorPresentation = AddSceneComponentIfMissing<WorldActorPresentationController>(
                presentation => presentation.Initialize(
                    worldActorController,
                    simulationClient,
                    runtimePrimitiveMaterial));
            AddSceneComponentIfMissing<CorpsePresentationController>(
                presentation => presentation.Initialize(
                    corpseController,
                    runtimePrimitiveMaterial,
                    sceneContext,
                    actorPresentation));
            AddSceneComponentIfMissing<WaterWakePresentation>(
                wakes => wakes.Initialize(
                    simulationClient,
                    sceneContext,
                    actorPresentation,
                    WaterSurfaces.FromScene(scene),
                    waterRingMaterial,
                    waterSprayMaterial));
            AddSceneComponentIfMissing<WorldInteractionTargetingController>(
                targeting => targeting.Initialize(
                    worldInteractionController,
                    worldActorController,
                    worldMenu,
                    gameMessages,
                    texts));
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
                    worldMenu,
                    texts));
            AddSceneComponentIfMissing<CombatProjectilePresentation>(
                presentation => presentation.Initialize(
                    combatController,
                    simulationClient,
                    combatantViews,
                    runtimePrimitiveMaterial,
                    arrowModel));
            AddSceneComponentIfMissing<CombatDeathPresentation>(
                presentation => presentation.Initialize(combatController, combatantViews));
            AddSceneComponentIfMissing<WorldNameplates>(
                nameplates => nameplates.Initialize(combatController, combatantViews));
            AddSceneComponentIfMissing<WorldQuestMarkers>(
                markers => markers.Initialize(questController, inventoryController, actorPresentation, combatantViews));
            AddSceneComponentIfMissing<TemporaryCombatHud>(
                hud => hud.Initialize(
                    combatController,
                    combatTargeting,
                    simulationClient,
                    combatantViews,
                    texts));
        }

        // The entry screen says when the served content is newer than this client reads.
        private string ClientNotice()
        {
            return contentReport != null && contentReport.UpdateRequired
                ? ClientMessages.Text(texts, contentReport.FallbackCode)
                : string.Empty;
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
