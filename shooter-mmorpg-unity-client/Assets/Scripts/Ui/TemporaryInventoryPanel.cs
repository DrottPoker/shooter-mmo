using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ShooterMmo.Gameplay;
using ShooterMmo.Items;
using ShooterMmo.WorldData.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ShooterMmo.Ui
{
    public enum InventoryPanelMode
    {
        FullDevelopment,
        CharacterAndEquipment,
        CharacterOnly
    }

    [DisallowMultipleComponent]
    public sealed class TemporaryInventoryPanel : MonoBehaviour
    {
        private static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.08f, 0.96f);
        private static readonly Color SlotColor = new Color(0.12f, 0.14f, 0.17f, 1f);
        private static readonly Color SpecializedSlotColor =
            new Color(0.12f, 0.20f, 0.22f, 1f);
        private static readonly Color SelectedColor = new Color(0.28f, 0.48f, 0.72f, 1f);
        private static readonly Color ErrorColor = new Color(1f, 0.45f, 0.35f, 1f);

        private InventoryClientController controller;
        private CorpseClientController corpseController;
        private LocalPlayerInput localPlayerInput;
        private ThirdPersonCameraController playerCamera;
        private GameObject canvasObject;
        private GameObject rootObject;
        private RectTransform equipmentPanel;
        private RectTransform contextPanel;
        private RectTransform characterPanel;
        private RectTransform equipmentContent;
        private RectTransform contextContent;
        private RectTransform characterContent;
        private Text equipmentHeader;
        private Text contextHeader;
        private Text characterHeader;
        private Font font;
        private InventoryDragCoordinator dragCoordinator;
        private bool isOpen;
        private InventoryPanelMode mode = InventoryPanelMode.FullDevelopment;
        private InventoryContextKind context = InventoryContextKind.None;
        private Guid selectedItemId;
        private bool splitMode;
        private bool destroyConfirmation;
        private string splitQuantityText = "1";
        private string corpseQuantityText = "1";
        private bool corpsePartialLoot;
        private string localStatus = "Drag an item onto a highlighted destination.";

        public bool IsOpen
        {
            get { return isOpen; }
        }

        public InventoryPanelMode Mode
        {
            get { return mode; }
        }

        public bool IsEquipmentModuleVisible
        {
            get { return isOpen && equipmentPanel != null && equipmentPanel.gameObject.activeSelf; }
        }

        public bool IsContextModuleVisible
        {
            get { return isOpen && contextPanel != null && contextPanel.gameObject.activeSelf; }
        }

        public bool IsCharacterModuleVisible
        {
            get { return isOpen && characterPanel != null && characterPanel.gameObject.activeSelf; }
        }

        private void Start()
        {
            controller = ShooterMmoClientBootstrap.InventoryController;
            if (controller != null)
            {
                controller.State.Changed += Rebuild;
            }

            corpseController = ShooterMmoClientBootstrap.CorpseController;
            if (corpseController != null)
            {
                corpseController.State.Changed += OnCorpseStateChanged;
            }

            FindPlayerPresentation();
            BuildUi();
            SetOpen(false);
        }

        private void Update()
        {
            if (localPlayerInput == null || playerCamera == null)
            {
                FindPlayerPresentation();
            }

            if (localPlayerInput != null)
            {
                if (localPlayerInput.ToggleCharacterInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.CharacterOnly);
                }
                else if (localPlayerInput.ToggleEquipmentInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.CharacterAndEquipment);
                }
                else if (localPlayerInput.ToggleInventoryPressedThisFrame)
                {
                    ToggleMode(InventoryPanelMode.FullDevelopment);
                }
            }

            if (isOpen
                && Keyboard.current != null
                && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                SetOpen(false);
            }
        }

        private void OnDisable()
        {
            if (isOpen)
            {
                SetOpen(false);
            }
        }

        private void OnDestroy()
        {
            if (controller != null)
            {
                controller.State.Changed -= Rebuild;
            }

            if (corpseController != null)
            {
                corpseController.State.Changed -= OnCorpseStateChanged;
            }

            if (playerCamera != null)
            {
                playerCamera.SetUiCursorReleased(false);
            }
        }

        public void SetOpen(bool value)
        {
            SetOpen(value, value ? InventoryPanelMode.FullDevelopment : mode);
        }

        public void SetOpen(bool value, InventoryPanelMode requestedMode)
        {
            isOpen = value;
            if (value)
            {
                mode = requestedMode;
                ApplyModeVisibility();
            }

            if (rootObject != null)
            {
                rootObject.SetActive(value);
            }

            if (playerCamera != null)
            {
                playerCamera.SetUiCursorReleased(value);
            }

            if (value)
            {
                controller = controller ?? ShooterMmoClientBootstrap.InventoryController;
                controller?.EnsureFullState();
                Rebuild();
            }
            else
            {
                if (corpseController?.State.ActiveView != null
                    && corpseController.State.PendingOperationId == Guid.Empty)
                {
                    corpseController.TryClose(out _);
                }

                dragCoordinator?.Cancel();
                ClearSelection();
            }
        }

        private void ToggleMode(InventoryPanelMode requestedMode)
        {
            if (isOpen && mode == requestedMode)
            {
                SetOpen(false, requestedMode);
                return;
            }

            dragCoordinator?.Cancel();
            ClearSelection();
            SetOpen(true, requestedMode);
        }

        private void OnCorpseStateChanged()
        {
            if (corpseController?.State.ActiveView != null)
            {
                context = InventoryContextKind.Corpse;
                if (!isOpen)
                {
                    SetOpen(true, InventoryPanelMode.FullDevelopment);
                    return;
                }
            }

            Rebuild();
        }

        private void FindPlayerPresentation()
        {
            var localPlayer = FindAnyObjectByType<LocalPlayerController>();
            localPlayerInput = localPlayer != null
                ? localPlayer.PlayerInput
                : FindAnyObjectByType<LocalPlayerInput>();
            playerCamera = localPlayer != null
                ? localPlayer.PlayerCamera
                : FindAnyObjectByType<ThirdPersonCameraController>();
            if (playerCamera != null && isOpen)
            {
                playerCamera.SetUiCursorReleased(true);
            }
        }

        private void BuildUi()
        {
            EnsureEventSystem();
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasObject = new GameObject(
                "TemporaryInventoryCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            dragCoordinator = canvasObject.AddComponent<InventoryDragCoordinator>();
            dragCoordinator.Configure(
                canvasObject.GetComponent<RectTransform>(),
                font);

            rootObject = CreateUiObject("InventoryRoot", canvasObject.transform);
            var rootRect = rootObject.GetComponent<RectTransform>();
            Stretch(rootRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var blocker = rootObject.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0.35f);

            equipmentContent = CreateSection(
                rootObject.transform,
                "Equipment",
                new Vector2(0.02f, 0.06f),
                new Vector2(0.31f, 0.95f),
                out equipmentPanel,
                out equipmentHeader);
            contextContent = CreateSection(
                rootObject.transform,
                "Context",
                new Vector2(0.33f, 0.53f),
                new Vector2(0.98f, 0.95f),
                out contextPanel,
                out contextHeader);
            characterContent = CreateSection(
                rootObject.transform,
                "Character Inventory",
                new Vector2(0.33f, 0.06f),
                new Vector2(0.98f, 0.51f),
                out characterPanel,
                out characterHeader);
            ApplyModeVisibility();
        }

        private void ApplyModeVisibility()
        {
            if (equipmentPanel == null || contextPanel == null || characterPanel == null)
            {
                return;
            }

            equipmentPanel.gameObject.SetActive(
                mode != InventoryPanelMode.CharacterOnly);
            contextPanel.gameObject.SetActive(
                mode == InventoryPanelMode.FullDevelopment);
            characterPanel.gameObject.SetActive(true);
        }

        private void Rebuild()
        {
            if (!isOpen
                || equipmentContent == null
                || contextContent == null
                || characterContent == null)
            {
                return;
            }

            ClearChildren(equipmentContent);
            ClearChildren(contextContent);
            ClearChildren(characterContent);
            if (controller == null || controller.State.Catalog == null)
            {
                equipmentHeader.text = "Equipment";
                contextHeader.text = "Context";
                characterHeader.text = "Character Inventory";
                var message = controller?.State.Error?.ToDisplayMessage()
                    ?? "Inventory catalog is unavailable.";
                CreateMessage(characterContent, message, ErrorColor);
                return;
            }

            var state = controller.State;
            dragCoordinator?.Cancel();
            if (selectedItemId != Guid.Empty
                && !state.TryFindItem(selectedItemId, out _, out _))
            {
                ClearSelection();
            }

            equipmentHeader.text = "Equipment";
            contextHeader.text = ContextTitle(context);
            characterHeader.text = BuildCharacterHeader(state);

            if (equipmentPanel.gameObject.activeSelf)
            {
                DrawEquipment(state);
            }

            if (contextPanel.gameObject.activeSelf)
            {
                DrawContext(state);
            }

            DrawCharacterInventory(state);
        }

        private void DrawEquipment(InventoryClientState state)
        {
            if (state.FullSnapshot == null)
            {
                CreateMessage(equipmentContent, StatusText(state), Color.white);
                return;
            }

            DrawStatus(equipmentContent, state);
            var grid = CreateGrid(equipmentContent, 1, 310f, 64f);
            foreach (var equipmentSlot in state.FullSnapshot.Equipment)
            {
                DrawEquipmentSlot(grid, state, equipmentSlot);
            }

            CreateActionBar(equipmentContent, state);
            CreateMessage(
                equipmentContent,
                "Non-empty Bag swaps are atomic. Drag a corpse Bag onto the occupied Bag equipment slot.",
                new Color(0.65f, 0.72f, 0.8f, 1f));
        }

        private void DrawContext(InventoryClientState state)
        {
            var tabs = CreateHorizontalRow(contextContent, 40f);
            CreateButton(tabs, "None", () => SetContext(InventoryContextKind.None), true);
            CreateButton(tabs, "Bank", () => SetContext(InventoryContextKind.Bank), true);
            CreateButton(
                tabs,
                "Recovery",
                () => SetContext(InventoryContextKind.RecoveryStorage),
                true);
            CreateButton(
                tabs,
                "Corpse",
                () => SetContext(InventoryContextKind.Corpse),
                true);
            CreateButton(tabs, "Refresh", RefreshCurrentContext, true);

            if (context == InventoryContextKind.None)
            {
                CreateMessage(
                    contextContent,
                    "Open Bank or Recovery Storage. Reads are account-owned; mutations use the active SimulationWorker access policy.",
                    Color.white);
                CreateMessage(
                    contextContent,
                    "Nearby corpse custody is discovered through SimulationWorker. Press E beside a corpse or open the Corpse tab.",
                    new Color(0.65f, 0.72f, 0.8f, 1f));
                return;
            }

            if (context == InventoryContextKind.Corpse)
            {
                DrawCorpse(contextContent, state);
                return;
            }

            DrawStatus(contextContent, state);
            if (context == InventoryContextKind.Bank)
            {
                DrawContainer(contextContent, state, state.Bank, "Character Bank", 4);
                return;
            }

            if (context == InventoryContextKind.RecoveryStorage)
            {
                DrawRecovery(contextContent, state);
            }
        }

        private void DrawCharacterInventory(InventoryClientState state)
        {
            DrawStatus(characterContent, state);
            if (state.FullSnapshot == null)
            {
                CreateMessage(characterContent, StatusText(state), Color.white);
                return;
            }

            DrawContainer(
                characterContent,
                state,
                state.FullSnapshot.PermanentInventory,
                "Permanent Inventory",
                5);
            if (state.FullSnapshot.EquippedBag == null)
            {
                CreateMessage(characterContent, "No Bag equipped.", new Color(0.75f, 0.75f, 0.75f));
            }
            else
            {
                DrawContainer(
                    characterContent,
                    state,
                    state.FullSnapshot.EquippedBag.Contents,
                    "Equipped Bag: " + state.Catalog.GetDisplayName(
                        state.FullSnapshot.EquippedBag.Item.DefinitionId),
                    4);
            }

            DrawContainer(
                characterContent,
                state,
                state.FullSnapshot.SecureContainer.Contents,
                state.FullSnapshot.SecureContainer.TierId,
                4);
        }

        private void DrawContainer(
            Transform parent,
            InventoryClientState state,
            InventoryContainer container,
            string title,
            int columns)
        {
            CreateSubheader(parent, title);
            if (container == null)
            {
                CreateMessage(parent, "Container state is unavailable.", ErrorColor);
                return;
            }

            var grid = CreateGrid(parent, columns, 150f, 72f);
            foreach (var slot in container.Slots)
            {
                DrawContainerSlot(grid, state, container, slot);
            }
        }

        private void DrawContainerSlot(
            Transform parent,
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot)
        {
            var item = slot.Item;
            var label = "Slot " + (slot.SlotIndex + 1);
            if (!string.Equals(slot.SlotKind, "general", StringComparison.Ordinal))
            {
                label += " [" + string.Join(", ", slot.AcceptedTags) + "]";
            }

            if (item != null)
            {
                label += "\n" + state.Catalog.GetDisplayName(item.DefinitionId)
                    + (item.Quantity > 1 ? " x" + item.Quantity : string.Empty);
            }

            var payload = item == null
                ? null
                : InventoryDragPayload.ForItem(item.ItemInstanceId);
            CreateSlotButton(
                parent,
                label,
                item,
                item != null && item.ItemInstanceId == selectedItemId,
                !string.Equals(slot.SlotKind, "general", StringComparison.Ordinal),
                item != null,
                () => OnContainerSlotClicked(slot),
                payload,
                state.CanMutate,
                (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnContainer(state, container, slot, candidate, out reason),
                candidate => OnContainerDrop(container, slot, candidate),
                RejectDrop);
        }

        private void DrawEquipmentSlot(
            Transform parent,
            InventoryClientState state,
            InventoryEquipmentSlot equipmentSlot)
        {
            var displayName = state.Catalog.TryGetEquipmentSlot(
                equipmentSlot.SlotId,
                out var definition)
                    ? definition.DisplayName
                    : equipmentSlot.SlotId;
            var label = displayName;
            if (equipmentSlot.Item != null)
            {
                label += "\n" + state.Catalog.GetDisplayName(
                    equipmentSlot.Item.DefinitionId);
            }

            var payload = equipmentSlot.Item == null
                ? null
                : InventoryDragPayload.ForItem(equipmentSlot.Item.ItemInstanceId);
            CreateSlotButton(
                parent,
                label,
                equipmentSlot.Item,
                equipmentSlot.Item != null
                    && equipmentSlot.Item.ItemInstanceId == selectedItemId,
                false,
                equipmentSlot.Item != null,
                () => OnEquipmentSlotClicked(equipmentSlot),
                payload,
                state.CanMutate,
                (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnEquipment(state, equipmentSlot, candidate, out reason),
                candidate => OnEquipmentDrop(equipmentSlot, candidate),
                RejectDrop);
        }

        private void DrawRecovery(Transform parent, InventoryClientState state)
        {
            var recovery = state.RecoveryStorage;
            if (recovery == null)
            {
                CreateMessage(parent, "Recovery Storage is unavailable.", Color.white);
                return;
            }

            if (recovery.Deliveries.Count == 0)
            {
                CreateMessage(parent, "Recovery Storage has no deliveries.", Color.white);
                return;
            }

            foreach (var delivery in recovery.Deliveries)
            {
                CreateSubheader(
                    parent,
                    delivery.SourceKind + "  revision " + delivery.Revision);
                var grid = CreateGrid(parent, 4, 150f, 72f);
                foreach (var deliveryItem in delivery.Items)
                {
                    var item = deliveryItem.Item;
                    var payload = InventoryDragPayload.ForRecoveryDelivery(
                        delivery.DeliveryId);
                    CreateSlotButton(
                        grid,
                        state.Catalog.GetDisplayName(item.DefinitionId)
                            + (item.Quantity > 1 ? " x" + item.Quantity : string.Empty),
                        item,
                        false,
                        false,
                        true,
                        null,
                        payload,
                        state.CanMutate,
                        RejectRecoveryDestination,
                        null,
                        RejectDrop);
                }

                CreateMessage(
                    parent,
                    "Drag any item in this delivery to Permanent Inventory or Bank. "
                        + "The complete Recovery delivery is claimed atomically.",
                    new Color(0.65f, 0.82f, 0.9f, 1f));
            }
        }

        private void DrawCorpse(Transform parent, InventoryClientState inventoryState)
        {
            corpseController = corpseController ?? ShooterMmoClientBootstrap.CorpseController;
            if (corpseController == null)
            {
                CreateMessage(parent, "Corpse interaction state is unavailable.", ErrorColor);
                return;
            }

            var corpseState = corpseController.State;
            if (corpseState.Error != null)
            {
                CreateMessage(parent, corpseState.Error.ToDisplayMessage(), ErrorColor);
            }

            if (corpseState.ActiveView == null)
            {
                CreateMessage(
                    parent,
                    "Move within 3 metres and press E, or open a nearby corpse below.",
                    Color.white);
                if (corpseState.NearbyCorpses.Count == 0)
                {
                    CreateMessage(parent, "No durable corpses are nearby.", Color.white);
                    return;
                }

                foreach (var corpse in corpseState.NearbyCorpses)
                {
                    var row = CreateHorizontalRow(parent, 38f);
                    var label = (string.IsNullOrWhiteSpace(corpse.SourceDisplayName)
                            ? "Corpse"
                            : corpse.SourceDisplayName)
                        + (corpse.IsEmpty ? " (empty)" : string.Empty);
                    CreateButton(
                        row,
                        "Open " + label,
                        () => ExecuteCorpseOpen(corpse.CorpseId),
                        corpseState.PendingOperationId == Guid.Empty);
                }

                return;
            }

            var view = corpseState.ActiveView;
            CreateMessage(
                parent,
                view.SourceDisplayName + "  |  Revision " + view.Revision
                    + "  |  Expires "
                    + DateTimeOffset.FromUnixTimeMilliseconds(
                        view.ExpiresAtUnixMilliseconds).ToLocalTime().ToString("HH:mm:ss"),
                Color.white);
            var actions = CreateHorizontalRow(parent, 38f);
            CreateButton(
                actions,
                "Close",
                () => ExecuteCorpseClose(),
                corpseState.PendingOperationId == Guid.Empty);
            CreateButton(
                actions,
                "Refresh",
                () => ExecuteCorpseRefresh(),
                corpseState.PendingOperationId == Guid.Empty);
            CreateButton(
                actions,
                corpsePartialLoot ? "Full Stack" : "Partial Stack",
                () =>
                {
                    corpsePartialLoot = !corpsePartialLoot;
                    Rebuild();
                },
                corpseState.PendingOperationId == Guid.Empty);
            var quantityInput = CreateInputField(actions, corpseQuantityText);
            quantityInput.onValueChanged.AddListener(value => corpseQuantityText = value);
            if (corpseState.Status == CorpseClientStatus.Busy)
            {
                CreateMessage(parent, "Waiting for authoritative corpse state.", SelectedColor);
            }

            foreach (var section in view.Sections)
            {
                CreateSubheader(
                    parent,
                    CorpseSectionTitle(section.SectionKind)
                        + "  revision " + section.ContainerRevision);
                var grid = CreateGrid(parent, 4, 150f, 72f);
                foreach (var slot in section.Slots)
                {
                    DrawCorpseSlot(grid, inventoryState, view, section, slot);
                }
            }

            CreateMessage(
                parent,
                "Drag items between corpse slots or between corpse and carried storage. Typed equipment slots enforce their shown type, compatible stacks merge, complete items that cannot merge swap, and occupied Bag equipment uses an atomic aggregate swap.",
                new Color(0.65f, 0.82f, 0.9f, 1f));
        }

        private void DrawCorpseSlot(
            Transform parent,
            InventoryClientState inventoryState,
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot)
        {
            var item = slot.Item;
            var inventoryItem = item?.ToInventoryItem();
            var label = CorpseSlotLabel(inventoryState.Catalog, slot);
            if (item != null)
            {
                label += "\n" + inventoryState.Catalog.GetDisplayName(item.DefinitionId)
                    + (item.Quantity > 1 ? " x" + item.Quantity : string.Empty);
            }

            var payload = item == null
                ? null
                : InventoryDragPayload.ForCorpseItem(view.CorpseId, item.ItemInstanceId);
            CreateSlotButton(
                parent,
                label,
                inventoryItem,
                false,
                !string.IsNullOrWhiteSpace(slot.EquipmentSlotId)
                    || !string.Equals(slot.SlotKind, "general", StringComparison.Ordinal),
                item != null,
                null,
                payload,
                corpseController.State.CanMutate,
                (InventoryDragPayload candidate, out string reason) =>
                    CanDropOnCorpse(
                        inventoryState,
                        view,
                        section,
                        slot,
                        candidate,
                        out reason),
                candidate => OnCorpseDrop(view, section, slot, candidate),
                RejectDrop);
        }

        private static string CorpseSectionTitle(string sectionKind)
        {
            return sectionKind switch
            {
                "equipment" => "Corpse Equipment",
                "general_inventory" => "Corpse Inventory",
                "bag" => "Corpse Bag Contents",
                _ => "Corpse " + sectionKind
            };
        }

        private static string CorpseSlotLabel(
            ClientItemCatalog catalog,
            CorpseLootSlot slot)
        {
            if (!string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
            {
                var displayName = catalog.TryGetEquipmentSlot(
                    slot.EquipmentSlotId,
                    out var equipmentSlot)
                    ? equipmentSlot.DisplayName
                    : slot.EquipmentSlotId;
                return "Equipment: " + displayName + " [" + slot.EquipmentSlotId + "]";
            }

            return "Slot " + (slot.SlotIndex + 1) + " [" + slot.SlotKind + "]";
        }

        private void CreateActionBar(Transform parent, InventoryClientState state)
        {
            CreateSubheader(parent, "Selected Item Actions");
            if (selectedItemId == Guid.Empty
                || !state.TryFindItem(selectedItemId, out var item, out var location))
            {
                CreateMessage(parent, "No item selected.", Color.white);
                CreateMessage(
                    parent,
                    localStatus,
                    new Color(0.8f, 0.85f, 0.92f, 1f));
                return;
            }

            CreateMessage(
                parent,
                state.Catalog.GetDisplayName(item.DefinitionId)
                    + "  quantity " + item.Quantity
                    + "  revision " + item.Revision
                    + "  source " + LocationLabel(location),
                SelectedColor);
            var splitRow = CreateHorizontalRow(parent, 38f);
            var input = CreateInputField(splitRow, splitQuantityText);
            input.onValueChanged.AddListener(value => splitQuantityText = value);
            CreateButton(
                splitRow,
                splitMode ? "Cancel Split" : "Split to Target",
                () =>
                {
                    splitMode = !splitMode;
                    destroyConfirmation = false;
                    localStatus = splitMode
                        ? "Drag the selected stack onto an empty compatible slot."
                        : "Split cancelled.";
                    Rebuild();
                },
                item.Quantity > 1 && state.CanMutate);
            CreateButton(
                splitRow,
                "Clear Selection",
                () =>
                {
                    ClearSelection();
                    Rebuild();
                },
                true);

            var destroyRow = CreateHorizontalRow(parent, 38f);
            var canDestroy = InventoryTargetAdvisor.CanDestroy(
                state.Catalog,
                item,
                out var destroyReason);
            CreateButton(
                destroyRow,
                destroyConfirmation ? "Confirm Destroy" : "Destroy",
                () => DestroySelected(item),
                canDestroy && state.CanMutate);
            if (!canDestroy)
            {
                CreateMessage(parent, destroyReason, ErrorColor);
            }

            CreateMessage(parent, localStatus, new Color(0.8f, 0.85f, 0.92f, 1f));
        }

        private bool CanDropOnContainer(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate)
            {
                reason = "Inventory mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                return CanDropCorpseItemOnContainer(
                    state,
                    container,
                    slot,
                    payload,
                    out reason);
            }

            if (payload.Kind == InventoryDragPayloadKind.RecoveryDelivery)
            {
                var delivery = state.RecoveryStorage?.Deliveries.FirstOrDefault(candidate =>
                    candidate.DeliveryId == payload.RecoveryDeliveryId);
                if (delivery == null)
                {
                    reason = "The Recovery delivery is no longer available.";
                    return false;
                }

                return InventoryTargetAdvisor.CanClaimRecovery(
                    state,
                    delivery,
                    container,
                    out reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "The dragged item is no longer available.";
                return false;
            }

            var splitDrag = IsSplitDrag(payload);
            if (slot.Item != null)
            {
                if (slot.Item.ItemInstanceId == item.ItemInstanceId)
                {
                    reason = "The item is already in this slot.";
                    return false;
                }

                if (splitDrag)
                {
                    reason = "Split stacks must be dropped into an empty slot.";
                    return false;
                }

                var targetLocation = new InventoryItemLocation(
                    InventoryItemLocationKind.Container,
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    string.Empty,
                    Guid.Empty);
                if (InventoryTargetAdvisor.CanMerge(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out reason))
                {
                    return true;
                }

                return InventoryTargetAdvisor.CanSwap(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out reason);
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                if (splitDrag)
                {
                    reason = "Equipped items cannot be split.";
                    return false;
                }

                return InventoryTargetAdvisor.CanUnequip(
                    state,
                    item,
                    container,
                    slot,
                    out reason);
            }

            var movedQuantity = item.Quantity;
            if (splitDrag
                && (!int.TryParse(splitQuantityText, out movedQuantity)
                    || !InventoryTargetAdvisor.CanSplit(
                        state.Catalog,
                        item,
                        movedQuantity,
                        out reason)))
            {
                return false;
            }

            return InventoryTargetAdvisor.CanPlaceInContainer(
                state,
                item,
                source,
                container,
                slot,
                movedQuantity,
                out reason);
        }

        private bool CanDropCorpseItemOnContainer(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            var view = corpseController?.State.ActiveView;
            if (view == null
                || !corpseController.State.CanMutate
                || view.CorpseId != payload.CorpseId
                || !view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out var sourceSection,
                    out var sourceSlot))
            {
                reason = "The corpse item is no longer available for mutation.";
                return false;
            }

            if (!IsCarriedLootDestination(state, container))
            {
                reason = "Corpse loot can enter only Permanent Inventory, the equipped Bag, or the Secure Container.";
                return false;
            }

            if (corpseItem.HasBagContents)
            {
                reason = "A corpse Bag aggregate must be dropped onto the occupied Bag equipment slot.";
                return false;
            }

            var quantity = corpseItem.Quantity;
            if (corpsePartialLoot
                && (!int.TryParse(corpseQuantityText, out quantity)
                    || quantity <= 0
                    || quantity >= corpseItem.Quantity))
            {
                reason = "Partial loot quantity must be greater than zero and smaller than the corpse stack.";
                return false;
            }

            var item = new InventoryItem(
                corpseItem.ItemInstanceId,
                corpseItem.DefinitionId,
                quantity,
                corpseItem.Revision,
                Array.Empty<InventoryPolicy>());
            var source = new InventoryItemLocation(
                InventoryItemLocationKind.Corpse,
                sourceSection.ContainerId,
                "corpse_" + sourceSection.SectionKind,
                sourceSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            if (slot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    item,
                    source,
                    container,
                    slot,
                    quantity,
                    out reason);
            }

            var target = new InventoryItemLocation(
                InventoryItemLocationKind.Container,
                container.ContainerId,
                container.ContainerType,
                slot.SlotIndex,
                string.Empty,
                Guid.Empty);
            if (InventoryTargetAdvisor.CanMerge(
                state,
                item,
                source,
                slot.Item,
                target,
                out reason))
            {
                return true;
            }

            if (corpsePartialLoot)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            if (!CanOccupyCorpseEquipmentSlot(
                    state.Catalog,
                    slot.Item,
                    sourceSlot,
                    out reason))
            {
                return false;
            }

            return InventoryTargetAdvisor.CanSwapWithExternalContainer(
                state,
                slot.Item,
                target,
                item,
                ToInventoryContainer(sourceSection),
                ToInventorySlot(sourceSlot),
                out reason);
        }

        private bool CanDropOnCorpse(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate
                || corpseController?.State.ActiveView == null
                || !corpseController.State.CanMutate
                || corpseController.State.ActiveView.CorpseId != view.CorpseId)
            {
                reason = "Inventory or corpse mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                return CanMoveCorpseItemWithinCorpse(
                    state,
                    view,
                    section,
                    slot,
                    payload,
                    out reason);
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "Only a current carried inventory item can enter corpse custody.";
                return false;
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                if (IsReverseCorpseBagSwap(state, item, source, section, slot))
                {
                    return true;
                }

                reason = "Move ordinary equipped items into carried storage before depositing them.";
                return false;
            }

            if (source.Kind != InventoryItemLocationKind.Container
                || !IsCarriedContainerType(source.ContainerType))
            {
                reason = "Bank and Recovery Storage items cannot enter a corpse.";
                return false;
            }

            if (slot.Item != null && slot.Item.HasBagContents)
            {
                reason = "An occupied corpse Bag aggregate must use the Bag equipment swap.";
                return false;
            }

            var quantity = item.Quantity;
            if (corpsePartialLoot
                && (!int.TryParse(corpseQuantityText, out quantity)
                    || quantity <= 0
                    || quantity >= item.Quantity))
            {
                reason = "Partial deposit quantity must be greater than zero and smaller than the carried stack.";
                return false;
            }

            if (!CanOccupyCorpseEquipmentSlot(
                    state.Catalog,
                    item,
                    slot,
                    out reason))
            {
                return false;
            }

            var corpseContainer = ToInventoryContainer(section);
            var corpseSlot = ToInventorySlot(slot);
            if (slot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    item,
                    source,
                    corpseContainer,
                    corpseSlot,
                    quantity,
                    out reason);
            }

            var movedItem = quantity == item.Quantity
                ? item
                : new InventoryItem(
                    item.ItemInstanceId,
                    item.DefinitionId,
                    quantity,
                    item.Revision,
                    item.Policies.ToArray());
            var corpseItem = slot.Item.ToInventoryItem();
            if (InventoryTargetAdvisor.CanMerge(
                    state.Catalog,
                    movedItem,
                    corpseItem,
                    out reason))
            {
                return true;
            }

            if (quantity != item.Quantity)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            return InventoryTargetAdvisor.CanSwapWithExternalContainer(
                state,
                item,
                source,
                corpseItem,
                corpseContainer,
                corpseSlot,
                out reason);
        }

        private bool CanMoveCorpseItemWithinCorpse(
            InventoryClientState state,
            CorpseLootView view,
            CorpseLootSection destinationSection,
            CorpseLootSlot destinationSlot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (payload.CorpseId != view.CorpseId
                || !view.TryFindItem(
                    payload.ItemInstanceId,
                    out var sourceItem,
                    out var sourceSection,
                    out var sourceSlot))
            {
                reason = "The corpse source item is no longer available.";
                return false;
            }

            if (sourceSection.ContainerId == destinationSection.ContainerId
                && sourceSlot.SlotIndex == destinationSlot.SlotIndex)
            {
                reason = "The corpse item is already in that slot.";
                return false;
            }

            if (view.BagHasContents(sourceItem)
                || view.BagHasContents(destinationSlot.Item))
            {
                reason = "A non-empty corpse Bag cannot use an ordinary internal slot move.";
                return false;
            }

            var quantity = sourceItem.Quantity;
            if (corpsePartialLoot
                && (!int.TryParse(corpseQuantityText, out quantity)
                    || quantity <= 0
                    || quantity >= sourceItem.Quantity))
            {
                reason = "Partial move quantity must be greater than zero and smaller than the corpse stack.";
                return false;
            }

            var sourceInventoryItem = sourceItem.ToInventoryItem();
            var movedItem = quantity == sourceItem.Quantity
                ? sourceInventoryItem
                : new InventoryItem(
                    sourceInventoryItem.ItemInstanceId,
                    sourceInventoryItem.DefinitionId,
                    quantity,
                    sourceInventoryItem.Revision,
                    sourceInventoryItem.Policies.ToArray());
            if (!CanOccupyCorpseEquipmentSlot(
                    state.Catalog,
                    movedItem,
                    destinationSlot,
                    out reason))
            {
                return false;
            }

            var destinationContainer = ToInventoryContainer(destinationSection);
            var sourceLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Corpse,
                sourceSection.ContainerId,
                CorpseContainerType(sourceSection.SectionKind),
                sourceSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            if (destinationSlot.Item == null)
            {
                return InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    movedItem,
                    sourceLocation,
                    destinationContainer,
                    ToInventorySlot(destinationSlot),
                    quantity,
                    out reason);
            }

            var targetItem = destinationSlot.Item.ToInventoryItem();
            if (InventoryTargetAdvisor.CanMerge(
                    state.Catalog,
                    movedItem,
                    targetItem,
                    out reason))
            {
                return true;
            }

            if (quantity != sourceItem.Quantity)
            {
                reason = "A partial stack can only enter an empty slot or merge with a compatible stack.";
                return false;
            }

            if (!CanOccupyCorpseEquipmentSlot(
                    state.Catalog,
                    targetItem,
                    sourceSlot,
                    out reason))
            {
                return false;
            }

            var emptyDestinationSlot = ToEmptyInventorySlot(destinationSlot);
            if (!InventoryTargetAdvisor.CanPlaceInContainer(
                    state,
                    sourceInventoryItem,
                    sourceLocation,
                    destinationContainer,
                    emptyDestinationSlot,
                    out reason))
            {
                return false;
            }

            var targetLocation = new InventoryItemLocation(
                InventoryItemLocationKind.Corpse,
                destinationSection.ContainerId,
                CorpseContainerType(destinationSection.SectionKind),
                destinationSlot.SlotIndex,
                string.Empty,
                Guid.Empty);
            return InventoryTargetAdvisor.CanPlaceInContainer(
                state,
                targetItem,
                targetLocation,
                ToInventoryContainer(sourceSection),
                ToEmptyInventorySlot(sourceSlot),
                out reason);
        }

        private static bool CanOccupyCorpseEquipmentSlot(
            ClientItemCatalog catalog,
            InventoryItem item,
            CorpseLootSlot slot,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(slot.EquipmentSlotId))
            {
                return true;
            }

            if (!catalog.TryGetEquipmentSlot(slot.EquipmentSlotId, out _)
                || !catalog.TryGetDefinition(item.DefinitionId, out var definition)
                || !ItemEquipmentRules.IsCompatible(definition, slot.EquipmentSlotId))
            {
                reason = "The item is not compatible with corpse equipment slot '"
                    + slot.EquipmentSlotId + "'.";
                return false;
            }

            return true;
        }

        private static bool IsCarriedLootDestination(
            InventoryClientState state,
            InventoryContainer container)
        {
            var snapshot = state.FullSnapshot;
            return snapshot != null
                && (snapshot.PermanentInventory.ContainerId == container.ContainerId
                    || snapshot.SecureContainer.Contents.ContainerId == container.ContainerId
                    || snapshot.EquippedBag?.Contents.ContainerId == container.ContainerId);
        }

        private bool CanDropOnEquipment(
            InventoryClientState state,
            InventoryEquipmentSlot equipmentSlot,
            InventoryDragPayload payload,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate)
            {
                reason = "Inventory mutation is unavailable while state is loading or busy.";
                return false;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                var view = corpseController?.State.ActiveView;
                if (view == null
                    || !corpseController.State.CanMutate
                    || view.CorpseId != payload.CorpseId
                    || !view.TryFindItem(
                        payload.ItemInstanceId,
                        out var corpseBag,
                        out var sourceSection,
                        out _))
                {
                    reason = "The corpse Bag is no longer available.";
                    return false;
                }

                if (!string.Equals(equipmentSlot.SlotId, "bag", StringComparison.Ordinal)
                    || equipmentSlot.Item == null
                    || state.FullSnapshot?.EquippedBag == null
                    || equipmentSlot.Item.ItemInstanceId
                        != state.FullSnapshot.EquippedBag.Item.ItemInstanceId
                    || !corpseBag.HasBagContents
                    || !string.Equals(
                        sourceSection.SectionKind,
                        "equipment",
                        StringComparison.Ordinal))
                {
                    reason = "Atomic corpse Bag swap requires both occupied Bag equipment aggregates.";
                    return false;
                }

                return true;
            }

            if (payload.Kind != InventoryDragPayloadKind.Item
                || !state.TryFindItem(payload.ItemInstanceId, out var item, out var source))
            {
                reason = "Only a current inventory item can be equipped.";
                return false;
            }

            if (IsSplitDrag(payload))
            {
                reason = "A split stack cannot be equipped.";
                return false;
            }

            if (equipmentSlot.Item != null)
            {
                reason = equipmentSlot.Item.ItemInstanceId == item.ItemInstanceId
                    ? "The item is already equipped in this slot."
                    : "The equipment slot is occupied.";
                return false;
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                reason = "Move the equipped item to an inventory slot before changing equipment slots.";
                return false;
            }

            return InventoryTargetAdvisor.CanEquip(
                state,
                item,
                source,
                equipmentSlot,
                out reason);
        }

        private void OnContainerDrop(
            InventoryContainer container,
            InventorySlot slot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnContainer(state, container, slot, payload, out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                SubmitCorpseLoot(container, slot, payload);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.RecoveryDelivery)
            {
                var delivery = state.RecoveryStorage.Deliveries.First(candidate =>
                    candidate.DeliveryId == payload.RecoveryDeliveryId);
                ClaimRecovery(delivery, container.ContainerId);
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out var source);
            if (slot.Item != null)
            {
                var targetLocation = new InventoryItemLocation(
                    InventoryItemLocationKind.Container,
                    container.ContainerId,
                    container.ContainerType,
                    slot.SlotIndex,
                    string.Empty,
                    Guid.Empty);
                if (!InventoryTargetAdvisor.CanMerge(
                    state,
                    item,
                    source,
                    slot.Item,
                    targetLocation,
                    out _))
                {
                    Execute(
                        controller.TrySwap(item, slot.Item, out var swapError),
                        swapError,
                        "Swap submitted.");
                    return;
                }

                Execute(
                    controller.TryMerge(item, slot.Item, out var mergeError),
                    mergeError,
                    "Merge submitted.");
                return;
            }

            bool sent;
            string operationError;
            if (IsSplitDrag(payload))
            {
                int.TryParse(splitQuantityText, out var quantity);
                sent = controller.TrySplit(
                    item,
                    quantity,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                sent = controller.TryUnequip(
                    item,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else
            {
                sent = controller.TryRelocate(
                    item,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }

            Execute(
                sent,
                operationError,
                IsSplitDrag(payload) ? "Split submitted." : "Move submitted.");
        }

        private void OnEquipmentDrop(
            InventoryEquipmentSlot equipmentSlot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnEquipment(state, equipmentSlot, payload, out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                var view = corpseController.State.ActiveView;
                view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseBag,
                    out _,
                    out _);
                ExecuteCorpse(
                    corpseController.TrySwapBag(corpseBag, out var corpseError),
                    corpseError,
                    "Atomic Bag swap submitted.");
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out _);
            Execute(
                controller.TryEquip(item, equipmentSlot.SlotId, out var error),
                error,
                "Equip submitted.");
        }

        private void OnContainerSlotClicked(InventorySlot slot)
        {
            ToggleActionSelection(slot.Item);
        }

        private void OnEquipmentSlotClicked(InventoryEquipmentSlot equipmentSlot)
        {
            ToggleActionSelection(equipmentSlot.Item);
        }

        private void ToggleActionSelection(InventoryItem item)
        {
            if (item == null)
            {
                localStatus = "Drag an item onto this slot to move it.";
                Rebuild();
                return;
            }

            if (selectedItemId == item.ItemInstanceId)
            {
                ClearSelection();
                localStatus = "Selection cleared. Drag an item to move it.";
                Rebuild();
                return;
            }

            SelectItem(item);
        }

        private bool IsSplitDrag(InventoryDragPayload payload)
        {
            return splitMode
                && payload.Kind == InventoryDragPayloadKind.Item
                && payload.ItemInstanceId == selectedItemId;
        }

        private static bool RejectRecoveryDestination(
            InventoryDragPayload payload,
            out string reason)
        {
            reason = "Recovery Storage accepts system deliveries only. "
                + "Drag a Recovery delivery to Permanent Inventory or Bank to claim it.";
            return false;
        }

        private void RejectDrop(string reason)
        {
            localStatus = string.IsNullOrWhiteSpace(reason)
                ? "This inventory destination is not valid."
                : reason;
            Rebuild();
        }

        private void ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            Execute(
                controller.TryClaimRecovery(delivery, destinationContainerId, out var error),
                error,
                "Recovery claim submitted.");
        }

        private void SubmitCorpseLoot(
            InventoryContainer destination,
            InventorySlot destinationSlot,
            InventoryDragPayload payload)
        {
            var view = corpseController?.State.ActiveView;
            if (view == null
                || !view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out _,
                    out _))
            {
                RejectDrop("The corpse item is no longer available.");
                return;
            }

            var quantity = corpseItem.Quantity;
            if (corpsePartialLoot)
            {
                int.TryParse(corpseQuantityText, out quantity);
            }

            ExecuteCorpse(
                corpseController.TryLoot(
                    corpseItem,
                    destination,
                    destinationSlot,
                    quantity,
                    out var error),
                error,
                quantity < corpseItem.Quantity
                    ? "Partial corpse loot submitted."
                    : "Corpse loot submitted.");
        }

        private void OnCorpseDrop(
            CorpseLootView view,
            CorpseLootSection section,
            CorpseLootSlot slot,
            InventoryDragPayload payload)
        {
            var state = controller.State;
            if (!CanDropOnCorpse(
                    state,
                    view,
                    section,
                    slot,
                    payload,
                    out var validationError))
            {
                RejectDrop(validationError);
                return;
            }

            if (payload.Kind == InventoryDragPayloadKind.CorpseItem)
            {
                view.TryFindItem(
                    payload.ItemInstanceId,
                    out var corpseItem,
                    out _,
                    out _);
                var corpseQuantity = corpseItem.Quantity;
                if (corpsePartialLoot)
                {
                    int.TryParse(corpseQuantityText, out corpseQuantity);
                }

                ExecuteCorpse(
                    corpseController.TryMoveWithinCorpse(
                        corpseItem,
                        section,
                        slot,
                        corpseQuantity,
                        out var moveError),
                    moveError,
                    corpseQuantity < corpseItem.Quantity
                        ? "Partial corpse rearrangement submitted."
                        : "Corpse rearrangement submitted.");
                return;
            }

            state.TryFindItem(payload.ItemInstanceId, out var item, out var source);
            if (IsReverseCorpseBagSwap(state, item, source, section, slot))
            {
                ExecuteCorpse(
                    corpseController.TrySwapBag(slot.Item, out var bagError),
                    bagError,
                    "Atomic Bag swap submitted.");
                return;
            }

            var quantity = item.Quantity;
            if (corpsePartialLoot)
            {
                int.TryParse(corpseQuantityText, out quantity);
            }

            ExecuteCorpse(
                corpseController.TryDeposit(
                    item,
                    section,
                    slot,
                    quantity,
                    out var error),
                error,
                quantity < item.Quantity
                    ? "Partial corpse deposit submitted."
                    : "Corpse deposit submitted.");
        }

        private static bool IsReverseCorpseBagSwap(
            InventoryClientState state,
            InventoryItem item,
            InventoryItemLocation source,
            CorpseLootSection section,
            CorpseLootSlot slot)
        {
            return item != null
                && source != null
                && source.Kind == InventoryItemLocationKind.Equipment
                && string.Equals(source.EquipmentSlotId, "bag", StringComparison.Ordinal)
                && state.FullSnapshot?.EquippedBag?.Item.ItemInstanceId == item.ItemInstanceId
                && string.Equals(section.SectionKind, "equipment", StringComparison.Ordinal)
                && slot.Item != null
                && slot.Item.HasBagContents;
        }

        private static InventoryContainer ToInventoryContainer(CorpseLootSection section)
        {
            return new InventoryContainer(
                section.ContainerId,
                CorpseContainerType(section.SectionKind),
                section.ContainerRevision,
                section.SlotCapacity,
                section.Slots.Select(ToInventorySlot).ToArray());
        }

        private static InventorySlot ToInventorySlot(CorpseLootSlot slot)
        {
            return new InventorySlot(
                slot.SlotIndex,
                slot.SlotKind,
                slot.AcceptedTags.ToArray(),
                slot.Item?.ToInventoryItem());
        }

        private static InventorySlot ToEmptyInventorySlot(CorpseLootSlot slot)
        {
            return new InventorySlot(
                slot.SlotIndex,
                slot.SlotKind,
                slot.AcceptedTags.ToArray(),
                null);
        }

        private static string CorpseContainerType(string sectionKind)
        {
            return sectionKind switch
            {
                "general_inventory" => "corpse_inventory",
                "equipment" => "corpse_equipment",
                "bag" => "corpse_bag_contents",
                _ => "corpse_inventory"
            };
        }

        private static bool IsCarriedContainerType(string containerType)
        {
            return string.Equals(containerType, "permanent_inventory", StringComparison.Ordinal)
                || string.Equals(containerType, "bag_contents", StringComparison.Ordinal)
                || string.Equals(containerType, "secure_container", StringComparison.Ordinal);
        }

        private void DestroySelected(InventoryItem item)
        {
            if (!destroyConfirmation)
            {
                destroyConfirmation = true;
                localStatus = "Press Confirm Destroy to send the authoritative destruction request.";
                Rebuild();
                return;
            }

            Execute(
                controller.TryDestroy(item, out var error),
                error,
                "Destroy submitted.");
        }

        private void Execute(bool sent, string error, string success)
        {
            localStatus = sent ? success : error;
            if (sent)
            {
                ClearSelection();
            }

            Rebuild();
        }

        private void ExecuteCorpse(bool sent, string error, string success)
        {
            localStatus = sent ? success : error;
            Rebuild();
        }

        private void SelectItem(InventoryItem item)
        {
            selectedItemId = item.ItemInstanceId;
            splitMode = false;
            destroyConfirmation = false;
            splitQuantityText = "1";
            localStatus = "Drag this item to move it, or use the selected-item actions.";
            Rebuild();
        }

        private void ClearSelection()
        {
            selectedItemId = Guid.Empty;
            splitMode = false;
            destroyConfirmation = false;
            splitQuantityText = "1";
        }

        private void SetContext(InventoryContextKind next)
        {
            context = next;
            ClearSelection();
            if (next is InventoryContextKind.Bank or InventoryContextKind.RecoveryStorage)
            {
                controller.RefreshContext(next);
            }

            Rebuild();
        }

        private void RefreshCurrentContext()
        {
            if (context == InventoryContextKind.Corpse)
            {
                if (corpseController?.State.ActiveView != null)
                {
                    ExecuteCorpseRefresh();
                }

                return;
            }

            controller.RefreshContext(context);
        }

        private void ExecuteCorpseOpen(Guid corpseId)
        {
            var error = "Corpse interaction state is unavailable.";
            if (corpseController == null
                || !corpseController.TryOpen(corpseId, out error))
            {
                RejectDrop(error);
                return;
            }

            localStatus = "Corpse open submitted.";
        }

        private void ExecuteCorpseClose()
        {
            var error = "Corpse interaction state is unavailable.";
            if (corpseController == null
                || !corpseController.TryClose(out error))
            {
                RejectDrop(error);
                return;
            }

            localStatus = "Corpse close submitted.";
        }

        private void ExecuteCorpseRefresh()
        {
            var error = "Corpse interaction state is unavailable.";
            if (corpseController == null
                || !corpseController.TryRefresh(out error))
            {
                RejectDrop(error);
                return;
            }

            localStatus = "Corpse refresh submitted.";
        }

        private void DrawStatus(Transform parent, InventoryClientState state)
        {
            if (state.Error != null)
            {
                CreateMessage(parent, state.Error.ToDisplayMessage(), ErrorColor);
            }

            if (state.Status is InventoryClientStatus.Loading or InventoryClientStatus.Busy)
            {
                CreateMessage(parent, StatusText(state), new Color(1f, 0.8f, 0.3f, 1f));
            }
        }

        private string BuildCharacterHeader(InventoryClientState state)
        {
            var snapshot = state.FullSnapshot;
            if (snapshot == null)
            {
                return "Character Inventory  |  " + StatusText(state);
            }

            return "Character Inventory  |  Weight " + snapshot.CarriedWeight
                + " / " + snapshot.CarryCapacity
                + "  |  Load " + (snapshot.LoadRatioBasisPoints / 100f).ToString("0.##")
                + "%  |  Movement "
                + (snapshot.MovementMultiplierBasisPoints / 100f).ToString("0.##")
                + "%  |  Sprint " + (snapshot.SprintEligible ? "Allowed" : "Blocked")
                + "  |  Revision " + snapshot.ItemStateRevision;
        }

        private static string ContextTitle(InventoryContextKind value)
        {
            return value switch
            {
                InventoryContextKind.Bank => "Context: Character Bank",
                InventoryContextKind.RecoveryStorage => "Context: Recovery Storage",
                InventoryContextKind.Corpse => "Context: Corpse",
                InventoryContextKind.WorldLootPrepared => "Context: World Loot (Prepared)",
                _ => "Context Container"
            };
        }

        private static string StatusText(InventoryClientState state)
        {
            return state.Status switch
            {
                InventoryClientStatus.Loading => "Loading authoritative inventory...",
                InventoryClientStatus.Busy => "Waiting for the committed operation and refresh...",
                InventoryClientStatus.UpdateRequired => "Client update required.",
                InventoryClientStatus.Error => "Inventory refresh failed.",
                InventoryClientStatus.Ready => "Inventory ready.",
                _ => "Inventory has not loaded."
            };
        }

        private static string LocationLabel(InventoryItemLocation location)
        {
            return location.Kind switch
            {
                InventoryItemLocationKind.Equipment => "equipment/" + location.EquipmentSlotId,
                InventoryItemLocationKind.RecoveryStorage => "recovery/" + location.RecoveryDeliveryId,
                _ => location.ContainerType + "/" + location.SlotIndex
            };
        }

        private RectTransform CreateSection(
            Transform parent,
            string title,
            Vector2 anchorMin,
            Vector2 anchorMax,
            out RectTransform panelRect,
            out Text header)
        {
            var panel = CreateUiObject(title + "Panel", parent);
            panelRect = panel.GetComponent<RectTransform>();
            Stretch(panelRect, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
            var background = panel.AddComponent<Image>();
            background.color = PanelColor;

            var headerObject = CreateUiObject("Header", panel.transform);
            var headerRect = headerObject.GetComponent<RectTransform>();
            Stretch(
                headerRect,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(12f, -42f),
                new Vector2(-12f, -6f));
            header = headerObject.AddComponent<Text>();
            ConfigureText(header, title, 18, TextAnchor.MiddleLeft, FontStyle.Bold);

            var scrollObject = CreateUiObject("Scroll", panel.transform);
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            Stretch(
                scrollRectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, 8f),
                new Vector2(-8f, -48f));
            var scrollRect = scrollObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;

            var viewport = CreateUiObject("Viewport", scrollObject.transform);
            var viewportRect = viewport.GetComponent<RectTransform>();
            Stretch(viewportRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewport.AddComponent<Mask>().showMaskGraphic = false;
            scrollRect.viewport = viewportRect;

            var content = CreateUiObject("Content", viewport.transform).GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(4, 4, 4, 4);
            layout.spacing = 6f;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = content;
            return content;
        }

        private RectTransform CreateGrid(
            Transform parent,
            int columns,
            float cellWidth,
            float cellHeight)
        {
            var gridObject = CreateUiObject("SlotGrid", parent);
            var grid = gridObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(cellWidth, cellHeight);
            grid.spacing = new Vector2(6f, 6f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Max(1, columns);
            grid.childAlignment = TextAnchor.UpperLeft;
            var count = 1;
            var layout = gridObject.AddComponent<LayoutElement>();
            layout.preferredHeight = cellHeight * count;
            StartCoroutine(UpdateGridHeightNextFrame(grid, layout, columns, cellHeight));
            return gridObject.GetComponent<RectTransform>();
        }

        private static IEnumerator UpdateGridHeightNextFrame(
            GridLayoutGroup grid,
            LayoutElement layout,
            int columns,
            float cellHeight)
        {
            yield return null;
            if (grid == null || layout == null)
            {
                yield break;
            }

            var rows = Mathf.Max(1, Mathf.CeilToInt(grid.transform.childCount / (float)columns));
            layout.preferredHeight = rows * cellHeight + Mathf.Max(0, rows - 1) * grid.spacing.y;
        }

        private GameObject CreateSlotButton(
            Transform parent,
            string label,
            InventoryItem item,
            bool selected,
            bool specialized,
            bool interactable,
            Action onClick,
            InventoryDragPayload dragPayload = null,
            bool canDrag = false,
            InventoryDropValidator dropValidator = null,
            Action<InventoryDragPayload> onDrop = null,
            Action<string> onRejected = null)
        {
            var buttonObject = CreateUiObject("Slot", parent);
            var image = buttonObject.AddComponent<Image>();
            var baseColor = selected
                ? SelectedColor
                : specialized ? SpecializedSlotColor : SlotColor;
            image.color = baseColor;
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.interactable = interactable;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            Sprite icon = null;
            if (item != null && controller?.State.Catalog != null)
            {
                icon = controller.State.Catalog.LoadIcon(item.DefinitionId);
                if (icon != null)
                {
                    var iconObject = CreateUiObject("Icon", buttonObject.transform);
                    var iconRect = iconObject.GetComponent<RectTransform>();
                    iconRect.anchorMin = new Vector2(0f, 0.5f);
                    iconRect.anchorMax = new Vector2(0f, 0.5f);
                    iconRect.pivot = new Vector2(0f, 0.5f);
                    iconRect.anchoredPosition = new Vector2(6f, 0f);
                    iconRect.sizeDelta = new Vector2(42f, 42f);
                    var iconImage = iconObject.AddComponent<Image>();
                    iconImage.sprite = icon;
                    iconImage.preserveAspect = true;
                    iconImage.raycastTarget = false;
                }
            }

            var textObject = CreateUiObject("Label", buttonObject.transform);
            var textRect = textObject.GetComponent<RectTransform>();
            Stretch(textRect, Vector2.zero, Vector2.one, new Vector2(8f, 4f), new Vector2(-6f, -4f));
            var text = textObject.AddComponent<Text>();
            ConfigureText(text, label, 12, TextAnchor.MiddleCenter, FontStyle.Normal);
            text.raycastTarget = false;
            if (dragPayload != null)
            {
                var source = buttonObject.AddComponent<InventoryDragSource>();
                source.Configure(
                    dragCoordinator,
                    dragPayload,
                    label,
                    icon,
                    canDrag);
            }

            if (dropValidator != null)
            {
                var target = buttonObject.AddComponent<InventoryDropTarget>();
                target.Configure(
                    dragCoordinator,
                    image,
                    baseColor,
                    dropValidator,
                    onDrop,
                    onRejected);
            }

            return buttonObject;
        }

        private RectTransform CreateHorizontalRow(Transform parent, float height)
        {
            var row = CreateUiObject("ActionRow", parent);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            var element = row.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            return row.GetComponent<RectTransform>();
        }

        private void CreateButton(
            Transform parent,
            string label,
            Action onClick,
            bool interactable)
        {
            var buttonObject = CreateUiObject(label + "Button", parent);
            var image = buttonObject.AddComponent<Image>();
            image.color = SlotColor;
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var text = CreateUiObject("Label", buttonObject.transform).AddComponent<Text>();
            Stretch(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(4f, 2f),
                new Vector2(-4f, -2f));
            ConfigureText(text, label, 13, TextAnchor.MiddleCenter, FontStyle.Bold);
            text.raycastTarget = false;
        }

        private InputField CreateInputField(Transform parent, string value)
        {
            var inputObject = CreateUiObject("SplitQuantity", parent);
            var image = inputObject.AddComponent<Image>();
            image.color = Color.white;
            var input = inputObject.AddComponent<InputField>();
            input.contentType = InputField.ContentType.IntegerNumber;
            input.lineType = InputField.LineType.SingleLine;
            var text = CreateUiObject("Text", inputObject.transform).AddComponent<Text>();
            Stretch(
                text.rectTransform,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, 2f),
                new Vector2(-8f, -2f));
            ConfigureText(text, value, 14, TextAnchor.MiddleLeft, FontStyle.Normal);
            text.color = Color.black;
            input.textComponent = text;
            input.text = value;
            var layout = inputObject.AddComponent<LayoutElement>();
            layout.preferredWidth = 80f;
            layout.flexibleWidth = 0f;
            return input;
        }

        private void CreateSubheader(Transform parent, string text)
        {
            var label = CreateUiObject("Subheader", parent).AddComponent<Text>();
            ConfigureText(label, text, 15, TextAnchor.MiddleLeft, FontStyle.Bold);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
        }

        private void CreateMessage(Transform parent, string text, Color color)
        {
            var label = CreateUiObject("Message", parent).AddComponent<Text>();
            ConfigureText(label, text, 13, TextAnchor.MiddleLeft, FontStyle.Normal);
            label.color = color;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.gameObject.AddComponent<LayoutElement>().minHeight = 28f;
        }

        private void ConfigureText(
            Text text,
            string value,
            int fontSize,
            TextAnchor alignment,
            FontStyle style)
        {
            text.font = font;
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = Color.white;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            var value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value;
        }

        private static void Stretch(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static void ClearChildren(Transform parent)
        {
            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                Destroy(parent.GetChild(index).gameObject);
            }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                return;
            }

            var eventSystem = new GameObject(
                "InventoryEventSystem",
                typeof(EventSystem),
                typeof(InputSystemUIInputModule));
            eventSystem.transform.SetAsFirstSibling();
        }
    }
}
