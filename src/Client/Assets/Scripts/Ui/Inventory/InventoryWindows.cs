using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // What opened the storage window, which decides its tabs and what closes it.
    public enum InventoryStorageSource
    {
        None,

        // The open interaction of an NPC that offers storage or trade.
        Npc,

        // An opened corpse.
        Corpse,

        // The Development Admin menu, while the development worker opens storage everywhere.
        Development
    }

    // Lets the Development Admin menu open the Bank or Recovery Storage without an NPC.
    public sealed class InventoryWindowRequests
    {
        public event Action<InventoryContextKind> StorageRequested;

        public void RequestStorage(InventoryContextKind context)
        {
            StorageRequested?.Invoke(context);
        }
    }

    // The inventory windows: Equipment, Inventory, and the storage window for a Bank, Recovery
    // Storage, a corpse, or a vendor. Each window opens and closes on its own and sits at a
    // fixed place: Equipment at the left edge, Inventory at the right edge, and storage to the
    // left of Inventory. The windows read the persistent client state and send every action
    // through InventoryInteractions; they own no item rule.
    [DisallowMultipleComponent]
    public sealed partial class InventoryWindows : MonoBehaviour
    {
        public const string EquipmentWindowName = "EquipmentWindow";
        public const string InventoryWindowName = "InventoryWindow";
        public const string StorageWindowName = "StorageWindow";

        // The inventory canvas draws above the world interaction panel and the combat HUD.
        public const int CanvasSortingOrder = 100;

        // How long an undone item move stays marked in red.
        private const float RollbackMarkSeconds = 1.5f;

        private UiTheme theme;
        private InventoryClientController controller;
        private CorpseClientController corpseController;
        private WorldInteractionClientController worldInteractionController;
        private CombatClientController combatController;
        private WorldMenuController worldMenu;
        private GameMessageLog gameMessages;
        private InventoryWindowRequests requests;
        private PlayerBodyLookSetting bodyLook;
        private InventoryInteractions interactions;
        private LocalPlayerInput localPlayerInput;
        private RectTransform root;
        private InventoryDragCoordinator dragCoordinator;
        private InventoryTooltipView tooltip;
        private UiDialog dialog;
        private EquipmentWindow equipment;
        private CharacterInventoryWindow inventory;
        private StorageWindow storage;
        private InventoryStorageSource storageSource;
        private InventoryContextKind storageContext = InventoryContextKind.None;
        private string searchQuery = string.Empty;
        private bool refreshDeferred;
        private readonly HashSet<Guid> rolledBackItemIds = new HashSet<Guid>();
        private float rollbackMarkUntil;

        public UiTheme Theme
        {
            get { return theme; }
        }

        public bool IsEquipmentOpen
        {
            get { return equipment != null && equipment.Window.IsOpen; }
        }

        public bool IsInventoryOpen
        {
            get { return inventory != null && inventory.Window.IsOpen; }
        }

        public bool IsStorageOpen
        {
            get { return storage != null && storage.Window.IsOpen; }
        }

        public bool IsAnyOpen
        {
            get { return IsEquipmentOpen || IsInventoryOpen || IsStorageOpen; }
        }

        public InventoryContextKind StorageContext
        {
            get { return storageContext; }
        }

        public InventoryStorageSource StorageSource
        {
            get { return storageSource; }
        }

        public string SearchQuery
        {
            get { return searchQuery; }
        }

        public UiDialog Dialog
        {
            get { return dialog; }
        }

        public InventoryDragCoordinator DragCoordinator
        {
            get { return dragCoordinator; }
        }

        public InventoryTooltipView Tooltip
        {
            get { return tooltip; }
        }

        internal InventoryInteractions Interactions
        {
            get { return interactions; }
        }

        internal InventoryClientState State
        {
            get { return controller != null ? controller.State : null; }
        }

        internal CorpseClientState CorpseState
        {
            get { return corpseController != null ? corpseController.State : null; }
        }

        internal WorldInteractionClientController WorldInteraction
        {
            get { return worldInteractionController; }
        }

        internal CombatClientController Combat
        {
            get { return combatController; }
        }

        internal PlayerBodyLookSetting BodyLook
        {
            get { return bodyLook; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            UiTheme uiTheme,
            InventoryClientController inventoryController,
            CorpseClientController corpses,
            WorldInteractionClientController worldInteraction,
            CombatClientController combat,
            WorldMenuController menu,
            GameMessageLog messages,
            InventoryWindowRequests windowRequests,
            PlayerBodyLookSetting lookSetting = null)
        {
            theme = uiTheme;
            controller = inventoryController;
            corpseController = corpses;
            worldInteractionController = worldInteraction;
            combatController = combat;
            worldMenu = menu;
            gameMessages = messages;
            requests = windowRequests;
            bodyLook = lookSetting;
            interactions = new InventoryInteractions(inventoryController, corpses, worldInteraction);
        }

        public void SetEquipmentOpen(bool open)
        {
            SetWindowOpen(equipment.Window, open);
        }

        public void SetInventoryOpen(bool open)
        {
            SetWindowOpen(inventory.Window, open);
        }

        // Opens the storage window on a context, and the Inventory window beside it, so a
        // transfer has its source and destination in view.
        public void OpenStorage(InventoryContextKind context, InventoryStorageSource source)
        {
            if (context == InventoryContextKind.None)
            {
                return;
            }

            storageSource = source;
            storageContext = context;
            if (context is InventoryContextKind.Bank or InventoryContextKind.RecoveryStorage)
            {
                controller?.EnsureFullState();
            }

            SetWindowOpen(inventory.Window, true);
            SetWindowOpen(storage.Window, true);
            Refresh();
        }

        // Switches the open storage window to another of its tabs.
        public void ShowStorageContext(InventoryContextKind context)
        {
            if (!StorageContexts().Contains(context))
            {
                return;
            }

            storageContext = context;
            if (context is InventoryContextKind.Bank or InventoryContextKind.RecoveryStorage)
            {
                controller?.EnsureFullState();
            }

            Refresh();
        }

        // Closes the storage window. An open corpse belongs to it, so closing it closes the
        // corpse unless the caller already closes the interaction itself.
        public void CloseStorage(bool closeCorpse = true)
        {
            if (closeCorpse
                && corpseController?.State.ActiveView != null
                && corpseController.State.PendingOperationId == Guid.Empty)
            {
                worldInteractionController?.TryCloseActive(out _);
            }

            storageSource = InventoryStorageSource.None;
            storageContext = InventoryContextKind.None;
            SetWindowOpen(storage.Window, false);
        }

        public void CloseAll()
        {
            dialog.Close();
            dragCoordinator.Cancel();
            CloseStorage();
            SetWindowOpen(equipment.Window, false);
            SetWindowOpen(inventory.Window, false);
        }

        public void SetSearch(string query)
        {
            var next = query ?? string.Empty;
            if (string.Equals(next, searchQuery, StringComparison.Ordinal))
            {
                return;
            }

            searchQuery = next;
            Refresh();
        }

        // The tabs the storage window offers: the capabilities of the open NPC interaction,
        // the corpse, or the Development storage.
        internal IReadOnlyList<InventoryContextKind> StorageContexts()
        {
            switch (storageSource)
            {
                case InventoryStorageSource.Corpse:
                    return new[] { InventoryContextKind.Corpse };
                case InventoryStorageSource.Development:
                    return new[] { InventoryContextKind.Bank, InventoryContextKind.RecoveryStorage };
                case InventoryStorageSource.Npc:
                {
                    var active = worldInteractionController?.State.ActiveInteraction;
                    if (active == null)
                    {
                        return Array.Empty<InventoryContextKind>();
                    }

                    var contexts = new List<InventoryContextKind>();
                    foreach (var capability in active.Capabilities)
                    {
                        var context = ContextOf(capability.Kind);
                        if (context != InventoryContextKind.None && !contexts.Contains(context))
                        {
                            contexts.Add(context);
                        }
                    }

                    return contexts;
                }

                default:
                    return Array.Empty<InventoryContextKind>();
            }
        }

        // The name of the NPC whose open interaction offers the shown storage or trade, or
        // empty.
        internal string StorageOwnerName()
        {
            var active = worldInteractionController?.State.ActiveInteraction;
            return storageSource == InventoryStorageSource.Npc && active != null
                ? active.DisplayName
                : string.Empty;
        }

        // Tells the player what an action did when it did not send: why nothing moved.
        internal void Report(InventoryInteractionResult result)
        {
            if (!result.Sent)
            {
                Say(result.Message);
            }

            Refresh();
        }

        internal void Say(string message)
        {
            gameMessages?.Add(message, Time.unscaledTime);
        }

        internal bool IsRolledBack(InventoryItem item)
        {
            return item != null && rolledBackItemIds.Contains(item.ItemInstanceId);
        }

        // Redraws the open windows from the current state, or after the drag when one is in
        // progress, so a confirmation never cancels it.
        internal void Refresh()
        {
            if (equipment == null)
            {
                return;
            }

            if (dragCoordinator != null && dragCoordinator.IsDragging)
            {
                refreshDeferred = true;
                return;
            }

            refreshDeferred = false;
            if (tooltip != null && !IsAnyOpen)
            {
                tooltip.Hide(null);
            }

            if (equipment.Window.IsOpen)
            {
                equipment.Refresh();
            }

            if (inventory.Window.IsOpen)
            {
                inventory.Refresh();
            }

            if (storage.Window.IsOpen)
            {
                storage.Refresh();
            }
        }

        private void Start()
        {
            if (controller != null)
            {
                controller.State.Changed += Refresh;
                controller.State.PredictionRolledBack += OnPredictionRolledBack;
            }

            if (corpseController != null)
            {
                corpseController.State.Changed += OnCorpseStateChanged;
            }

            if (worldInteractionController != null)
            {
                worldInteractionController.InventoryCapabilityOpened += OnInventoryCapabilityOpened;
                worldInteractionController.State.Changed += OnWorldInteractionStateChanged;
            }

            if (combatController != null)
            {
                combatController.State.Changed += OnCombatStateChanged;
            }

            if (bodyLook != null)
            {
                bodyLook.Changed += Refresh;
            }

            if (requests != null)
            {
                requests.StorageRequested += OnStorageRequested;
            }

            FindPlayerInput();
            BuildUi();
        }

        private void Update()
        {
            if (localPlayerInput == null)
            {
                FindPlayerInput();
            }

            if (rolledBackItemIds.Count > 0 && Time.unscaledTime >= rollbackMarkUntil)
            {
                rolledBackItemIds.Clear();
                refreshDeferred = true;
            }

            if (refreshDeferred && !dragCoordinator.IsDragging)
            {
                Refresh();
            }

            if (localPlayerInput == null)
            {
                return;
            }

            // A key typed into the search field is text, not a window key.
            var typing = inventory != null && inventory.IsTyping;
            if (!typing && (localPlayerInput.ToggleCharacterInventoryPressedThisFrame
                || localPlayerInput.ToggleInventoryPressedThisFrame))
            {
                SetInventoryOpen(!IsInventoryOpen);
            }
            else if (!typing && localPlayerInput.ToggleEquipmentInventoryPressedThisFrame)
            {
                SetEquipmentOpen(!IsEquipmentOpen);
            }
            else if (localPlayerInput.CancelPressedThisFrame
                && (worldMenu == null || !worldMenu.CancelConsumedThisFrame))
            {
                // The targeting controller closes the interaction on Cancel. An Escape that
                // only closed the world menu leaves the windows open.
                if (dialog.IsOpen)
                {
                    dialog.Close();
                }
                else if (IsAnyOpen)
                {
                    dragCoordinator.Cancel();
                    CloseStorage(false);
                    SetWindowOpen(equipment.Window, false);
                    SetWindowOpen(inventory.Window, false);
                }
            }
        }

        private void OnDisable()
        {
            if (equipment != null && IsAnyOpen)
            {
                CloseAll();
            }
        }

        private void OnDestroy()
        {
            if (controller != null)
            {
                controller.State.Changed -= Refresh;
                controller.State.PredictionRolledBack -= OnPredictionRolledBack;
            }

            if (corpseController != null)
            {
                corpseController.State.Changed -= OnCorpseStateChanged;
            }

            if (worldInteractionController != null)
            {
                worldInteractionController.InventoryCapabilityOpened -= OnInventoryCapabilityOpened;
                worldInteractionController.State.Changed -= OnWorldInteractionStateChanged;
            }

            if (combatController != null)
            {
                combatController.State.Changed -= OnCombatStateChanged;
            }

            if (bodyLook != null)
            {
                bodyLook.Changed -= Refresh;
            }

            if (requests != null)
            {
                requests.StorageRequested -= OnStorageRequested;
            }

            if (dragCoordinator != null)
            {
                dragCoordinator.DroppedOutside -= OnDroppedOutside;
            }
        }

        private void SetWindowOpen(UiWindow window, bool open)
        {
            if (window == null || window.IsOpen == open)
            {
                return;
            }

            if (!open)
            {
                dragCoordinator.Cancel();
                dialog.Close();
            }

            window.SetOpen(open);
            if (open)
            {
                controller?.EnsureFullState();
            }

            Refresh();
        }

        // Every shot changes the live arrow count; the drawn slot updates in place, so a shot
        // never redraws the window or cancels a drag.
        private void OnCombatStateChanged()
        {
            if (equipment != null && equipment.Window.IsOpen)
            {
                equipment.UpdateLiveAmmunition();
            }
        }

        // The undone items flash red where they returned to, and the game says why.
        private void OnPredictionRolledBack(InventoryPredictionRollback rollback)
        {
            rolledBackItemIds.Clear();
            rolledBackItemIds.UnionWith(rollback.ItemInstanceIds);
            rollbackMarkUntil = Time.unscaledTime + RollbackMarkSeconds;
            Say("Item move undone: " + rollback.Error.Message);
            Refresh();
        }

        private void OnCorpseStateChanged()
        {
            var corpseOpen = corpseController?.State.ActiveView != null;
            if (corpseOpen && storageSource != InventoryStorageSource.Corpse)
            {
                OpenStorage(InventoryContextKind.Corpse, InventoryStorageSource.Corpse);
                return;
            }

            // The corpse closed, by the player, the server, or its expiry. The window closes
            // with it, and the game says why, such as that the corpse is empty.
            if (!corpseOpen && storageSource == InventoryStorageSource.Corpse)
            {
                CloseStorage(false);
                var corpse = corpseController.State;
                Say(corpse.Error != null ? corpse.Error.ToDisplayMessage() : corpse.Notice);
            }

            Refresh();
        }

        private void OnInventoryCapabilityOpened(RealtimeWorldActorCapability capability)
        {
            var context = ContextOf(capability.Kind);
            if (context == InventoryContextKind.None)
            {
                return;
            }

            if (context == InventoryContextKind.Vendor)
            {
                storage.SelectVendorCapability(capability.Id);
            }

            OpenStorage(context, InventoryStorageSource.Npc);
        }

        // Trade results arrive through the interaction state, and the window an NPC opened
        // closes when its interaction ends.
        private void OnWorldInteractionStateChanged()
        {
            if (storageSource == InventoryStorageSource.Npc
                && worldInteractionController?.State.ActiveInteraction == null)
            {
                CloseStorage(false);
                return;
            }

            if (storage != null && storage.Window.IsOpen)
            {
                Refresh();
            }
        }

        private void OnStorageRequested(InventoryContextKind context)
        {
            OpenStorage(context, InventoryStorageSource.Development);
        }

        private void FindPlayerInput()
        {
            var localPlayer = FindAnyObjectByType<LocalPlayerController>();
            localPlayerInput = localPlayer != null
                ? localPlayer.PlayerInput
                : FindAnyObjectByType<LocalPlayerInput>();
        }

        private void BuildUi()
        {
            if (theme == null)
            {
                theme = UiTheme.Load(out _);
            }

            TemporaryUiKit.EnsureEventSystem();
            var canvasObject = new GameObject(
                "InventoryCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            dragCoordinator = canvasObject.AddComponent<InventoryDragCoordinator>();
            dragCoordinator.Configure(canvasObject.GetComponent<RectTransform>(), theme);
            dragCoordinator.DroppedOutside += OnDroppedOutside;

            // The root only groups the windows and draws nothing, so a click beside or between
            // them still reaches the world.
            root = UiFactory.Create("InventoryRoot", canvasObject.transform);
            UiFactory.Fill(root);
            tooltip = InventoryTooltipView.Create(theme, root, dragCoordinator);
            equipment = new EquipmentWindow(this, root);
            inventory = new CharacterInventoryWindow(this, root);
            storage = new StorageWindow(this, root);
            equipment.Window.Place(new Vector2(0f, 1f), new Vector2(theme.ScreenMargin, -theme.WindowTop));
            inventory.Window.Place(new Vector2(1f, 1f), new Vector2(-theme.ScreenMargin, -theme.WindowTop));
            storage.Window.Place(
                new Vector2(1f, 1f),
                new Vector2(-(theme.ScreenMargin + inventory.Window.Rect.sizeDelta.x + theme.WindowGap), -theme.WindowTop));
            var maximumBody = 1080f - theme.WindowTop - theme.ScreenMargin - 220f;
            inventory.Window.SetMaximumBodyHeight(maximumBody);
            storage.Window.SetMaximumBodyHeight(maximumBody);
            equipment.Window.CloseRequested += () => SetEquipmentOpen(false);
            inventory.Window.CloseRequested += () => SetInventoryOpen(false);
            storage.Window.CloseRequested += () => CloseStorage();
            tooltip.transform.SetAsLastSibling();
            dialog = UiDialog.Create(theme, root);
            equipment.Window.SetOpen(false);
            inventory.Window.SetOpen(false);
            storage.Window.SetOpen(false);
        }

        private static InventoryContextKind ContextOf(RealtimeWorldActorCapabilityKind kind)
        {
            return kind switch
            {
                RealtimeWorldActorCapabilityKind.Bank => InventoryContextKind.Bank,
                RealtimeWorldActorCapabilityKind.RecoveryStorage => InventoryContextKind.RecoveryStorage,
                RealtimeWorldActorCapabilityKind.Vendor => InventoryContextKind.Vendor,
                _ => InventoryContextKind.None
            };
        }
    }
}
