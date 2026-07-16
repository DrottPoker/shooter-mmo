using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ShooterMmo.Gameplay;
using ShooterMmo.Items;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace ShooterMmo.Ui
{
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
        private LocalPlayerInput localPlayerInput;
        private ThirdPersonCameraController playerCamera;
        private GameObject canvasObject;
        private GameObject rootObject;
        private RectTransform equipmentContent;
        private RectTransform contextContent;
        private RectTransform characterContent;
        private Text equipmentHeader;
        private Text contextHeader;
        private Text characterHeader;
        private Font font;
        private bool isOpen;
        private InventoryContextKind context = InventoryContextKind.None;
        private Guid selectedItemId;
        private bool splitMode;
        private bool destroyConfirmation;
        private string splitQuantityText = "1";
        private string localStatus = "Select an item, then choose an authoritative target.";

        public bool IsOpen
        {
            get { return isOpen; }
        }

        private void Start()
        {
            controller = ShooterMmoClientBootstrap.InventoryController;
            if (controller != null)
            {
                controller.State.Changed += Rebuild;
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

            if (localPlayerInput != null
                && localPlayerInput.ToggleInventoryPressedThisFrame)
            {
                SetOpen(!isOpen);
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

            if (playerCamera != null)
            {
                playerCamera.SetUiCursorReleased(false);
            }
        }

        public void SetOpen(bool value)
        {
            isOpen = value;
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
                ClearSelection();
            }
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
                out equipmentHeader);
            contextContent = CreateSection(
                rootObject.transform,
                "Context",
                new Vector2(0.33f, 0.53f),
                new Vector2(0.98f, 0.95f),
                out contextHeader);
            characterContent = CreateSection(
                rootObject.transform,
                "Character Inventory",
                new Vector2(0.33f, 0.06f),
                new Vector2(0.98f, 0.51f),
                out characterHeader);
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
            if (selectedItemId != Guid.Empty
                && !state.TryFindItem(selectedItemId, out _, out _))
            {
                ClearSelection();
            }

            equipmentHeader.text = "Equipment";
            contextHeader.text = ContextTitle(context);
            characterHeader.text = BuildCharacterHeader(state);

            DrawEquipment(state);
            DrawContext(state);
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
                "Atomic non-empty Bag swaps are prepared at the state boundary and activate with corpse custody in a later phase.",
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
            CreateButton(tabs, "Refresh", () => controller.RefreshContext(context), true);

            if (context == InventoryContextKind.None)
            {
                CreateMessage(
                    contextContent,
                    "Open Bank or Recovery Storage. Reads are account-owned; mutations remain validated by the active SimulationWorker service point.",
                    Color.white);
                CreateMessage(
                    contextContent,
                    "Corpse and world-loot adapters are reserved in InventoryContextKind and activate only when their later authoritative snapshots exist.",
                    new Color(0.65f, 0.72f, 0.8f, 1f));
                return;
            }

            DrawStatus(contextContent, state);
            if (context == InventoryContextKind.Bank)
            {
                DrawContainer(contextContent, state, state.Bank, "Character Bank", 4);
                return;
            }

            DrawRecovery(contextContent, state);
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

            var interactable = CanUseContainerSlot(
                state,
                container,
                slot,
                out var blockedReason);
            var button = CreateSlotButton(
                parent,
                label,
                item,
                item != null && item.ItemInstanceId == selectedItemId,
                !string.Equals(slot.SlotKind, "general", StringComparison.Ordinal),
                interactable,
                () => OnContainerSlotClicked(container, slot));
            if (!interactable && !string.IsNullOrWhiteSpace(blockedReason))
            {
                button.GetComponentInChildren<Text>().text += "\nBlocked";
            }
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

            var interactable = CanUseEquipmentSlot(state, equipmentSlot, out _);
            CreateSlotButton(
                parent,
                label,
                equipmentSlot.Item,
                equipmentSlot.Item != null
                    && equipmentSlot.Item.ItemInstanceId == selectedItemId,
                false,
                interactable,
                () => OnEquipmentSlotClicked(equipmentSlot));
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
                    CreateSlotButton(
                        grid,
                        state.Catalog.GetDisplayName(item.DefinitionId)
                            + (item.Quantity > 1 ? " x" + item.Quantity : string.Empty),
                        item,
                        false,
                        false,
                        false,
                        null);
                }

                var actions = CreateHorizontalRow(parent, 38f);
                var canClaim = state.CanMutate && delivery.Items.Count > 0;
                var canClaimPermanent = canClaim
                    && InventoryTargetAdvisor.CanClaimRecovery(
                        state,
                        delivery,
                        state.FullSnapshot.PermanentInventory,
                        out _);
                CreateButton(
                    actions,
                    "Claim to Permanent",
                    () => ClaimRecovery(
                        delivery,
                        state.FullSnapshot.PermanentInventory.ContainerId),
                    canClaimPermanent);
                var canClaimBank = canClaim
                    && state.Bank != null
                    && InventoryTargetAdvisor.CanClaimRecovery(
                        state,
                        delivery,
                        state.Bank,
                        out _);
                CreateButton(
                    actions,
                    "Claim to Bank",
                    () => ClaimRecovery(delivery, state.Bank.ContainerId),
                    canClaimBank);
            }
        }

        private void CreateActionBar(Transform parent, InventoryClientState state)
        {
            CreateSubheader(parent, "Selected Item Actions");
            if (selectedItemId == Guid.Empty
                || !state.TryFindItem(selectedItemId, out var item, out var location))
            {
                CreateMessage(parent, "No item selected.", Color.white);
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
                        ? "Choose an empty compatible slot for the split stack."
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

        private bool CanUseContainerSlot(
            InventoryClientState state,
            InventoryContainer container,
            InventorySlot slot,
            out string reason)
        {
            reason = string.Empty;
            if (!state.CanMutate)
            {
                return slot.Item != null && selectedItemId == Guid.Empty;
            }

            if (selectedItemId == Guid.Empty)
            {
                return slot.Item != null;
            }

            if (!state.TryFindItem(selectedItemId, out var selected, out var source))
            {
                return false;
            }

            if (slot.Item != null)
            {
                if (slot.Item.ItemInstanceId == selectedItemId)
                {
                    return true;
                }

                return !splitMode && InventoryTargetAdvisor.CanMerge(
                    state,
                    selected,
                    source,
                    slot.Item,
                    new InventoryItemLocation(
                        InventoryItemLocationKind.Container,
                        container.ContainerId,
                        container.ContainerType,
                        slot.SlotIndex,
                        string.Empty,
                        Guid.Empty),
                    out reason);
            }

            if (splitMode
                && (!int.TryParse(splitQuantityText, out var quantity)
                    || !InventoryTargetAdvisor.CanSplit(
                        state.Catalog,
                        selected,
                        quantity,
                        out reason)))
            {
                return false;
            }

            if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                return InventoryTargetAdvisor.CanUnequip(
                    state,
                    selected,
                    container,
                    slot,
                    out reason);
            }

            var movedQuantity = selected.Quantity;
            if (splitMode && !int.TryParse(splitQuantityText, out movedQuantity))
            {
                reason = "The split quantity is invalid.";
                return false;
            }

            return InventoryTargetAdvisor.CanPlaceInContainer(
                state,
                selected,
                source,
                container,
                slot,
                movedQuantity,
                out reason);
        }

        private bool CanUseEquipmentSlot(
            InventoryClientState state,
            InventoryEquipmentSlot equipmentSlot,
            out string reason)
        {
            reason = string.Empty;
            if (selectedItemId == Guid.Empty)
            {
                return equipmentSlot.Item != null;
            }

            if (equipmentSlot.Item != null)
            {
                return equipmentSlot.Item.ItemInstanceId == selectedItemId;
            }

            return state.CanMutate
                && !splitMode
                && state.TryFindItem(selectedItemId, out var item, out var source)
                && source.Kind != InventoryItemLocationKind.Equipment
                && InventoryTargetAdvisor.CanEquip(
                    state,
                    item,
                    source,
                    equipmentSlot,
                    out reason);
        }

        private void OnContainerSlotClicked(
            InventoryContainer container,
            InventorySlot slot)
        {
            var state = controller.State;
            if (selectedItemId == Guid.Empty)
            {
                if (slot.Item != null)
                {
                    SelectItem(slot.Item);
                }

                return;
            }

            if (!state.TryFindItem(selectedItemId, out var selected, out var source))
            {
                ClearSelection();
                Rebuild();
                return;
            }

            if (slot.Item != null)
            {
                if (slot.Item.ItemInstanceId == selectedItemId)
                {
                    ClearSelection();
                    Rebuild();
                    return;
                }

                Execute(
                    controller.TryMerge(selected, slot.Item, out var error),
                    error,
                    "Merge submitted.");
                return;
            }

            bool sent;
            string operationError;
            if (splitMode && int.TryParse(splitQuantityText, out var quantity))
            {
                sent = controller.TrySplit(
                    selected,
                    quantity,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else if (source.Kind == InventoryItemLocationKind.Equipment)
            {
                sent = controller.TryUnequip(
                    selected,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }
            else
            {
                sent = controller.TryRelocate(
                    selected,
                    container.ContainerId,
                    slot.SlotIndex,
                    out operationError);
            }

            Execute(sent, operationError, splitMode ? "Split submitted." : "Move submitted.");
        }

        private void OnEquipmentSlotClicked(InventoryEquipmentSlot equipmentSlot)
        {
            var state = controller.State;
            if (selectedItemId == Guid.Empty)
            {
                if (equipmentSlot.Item != null)
                {
                    SelectItem(equipmentSlot.Item);
                }

                return;
            }

            if (equipmentSlot.Item != null)
            {
                ClearSelection();
                Rebuild();
                return;
            }

            if (!state.TryFindItem(selectedItemId, out var selected, out _))
            {
                ClearSelection();
                Rebuild();
                return;
            }

            Execute(
                controller.TryEquip(selected, equipmentSlot.SlotId, out var error),
                error,
                "Equip submitted.");
        }

        private void ClaimRecovery(RecoveryDelivery delivery, Guid destinationContainerId)
        {
            Execute(
                controller.TryClaimRecovery(delivery, destinationContainerId, out var error),
                error,
                "Recovery claim submitted.");
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

        private void SelectItem(InventoryItem item)
        {
            selectedItemId = item.ItemInstanceId;
            splitMode = false;
            destroyConfirmation = false;
            splitQuantityText = "1";
            localStatus = "Choose an enabled destination or item action.";
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
            controller.RefreshContext(next);
            Rebuild();
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
                InventoryContextKind.CorpsePrepared => "Context: Corpse (Prepared)",
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
            out Text header)
        {
            var panel = CreateUiObject(title + "Panel", parent);
            var panelRect = panel.GetComponent<RectTransform>();
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
            Action onClick)
        {
            var buttonObject = CreateUiObject("Slot", parent);
            var image = buttonObject.AddComponent<Image>();
            image.color = selected
                ? SelectedColor
                : specialized ? SpecializedSlotColor : SlotColor;
            var button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.interactable = interactable;
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            if (item != null && controller?.State.Catalog != null)
            {
                var icon = controller.State.Catalog.LoadIcon(item.DefinitionId);
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
