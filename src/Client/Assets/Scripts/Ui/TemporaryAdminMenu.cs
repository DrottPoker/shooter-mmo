using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The Admin menu (F3): development tools for playing and testing. Look steps the stored body
    // model and look; Movement sets the walk speed and teleports; Combat fills health and mana and makes
    // the character invulnerable. Only a worker in the Development environment allows them, which
    // it says by sending the admin state after the join; without it the menu explains that the
    // tools are off. While the menu is open, the world menu offers Teleport here on the ground,
    // and after Pick a spot a left click on the ground teleports.
    public sealed class TemporaryAdminMenu : MonoBehaviour, IWorldMenuProvider
    {
        public const string WindowName = "AdminMenu";
        public const string TeleportVerb = "Teleport here";

        // The walk speeds the menu offers, in percent of the World's.
        public static readonly int[] SpeedChoices = { 100, 200, 300, 500, 1000 };

        private const int TeleportAction = 1;

        // A teleport lands on the ground point, a little above it so the worker finds the ground.
        private const float TeleportLift = 0.05f;

        private readonly Dictionary<Guid, string> pendingDescriptions = new Dictionary<Guid, string>();
        private readonly List<Button> speedButtons = new List<Button>();
        private readonly List<CanvasGroup> toolPages = new List<CanvasGroup>();
        private RealtimeSimulationClient simulationClient;
        private PlayerBodyLookSetting bodyLook;
        private WorldMenuController worldMenu;
        private CombatClientController combat;
        private LocalPlayerController localPlayer;
        private TemporaryMenuWindow window;
        private RectTransform lookPage;
        private Text speedValue;
        private Button pickButton;
        private Text pickLabel;
        private Text healthValue;
        private Toggle invulnerableToggle;
        private BodyLook rowsLook;
        private string rowsModel;
        private BodyAppearanceCatalog rowsCatalog;
        private bool rowsReceived;
        private bool lastAvailable;
        private string resultText;
        private Color resultColor;

        public bool IsOpen
        {
            get { return window != null && window.IsVisible; }
        }

        // The next left click on the ground teleports.
        public bool IsPickingTeleport { get; private set; }

        // The worker allows the admin tools for this session.
        public bool ToolsAvailable
        {
            get { return simulationClient != null && simulationClient.IsJoined && simulationClient.AdminState != null; }
        }

        public TemporaryMenuWindow Window
        {
            get { return window; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            PlayerBodyLookSetting lookSetting,
            WorldMenuController menu,
            CombatClientController combatController)
        {
            simulationClient = realtimeClient;
            bodyLook = lookSetting;
            worldMenu = menu;
            combat = combatController;
        }

        public void SetOpen(bool open)
        {
            if (window == null)
            {
                return;
            }

            window.SetVisible(open);
            if (!open)
            {
                SetPicking(false);
            }

            RefreshAll();
        }

        // Asks the worker for a walk speed in percent of the World's.
        public bool RequestSpeed(int percent)
        {
            return Send(
                RealtimeAdminIntent.CreateSetMovementSpeed(Guid.NewGuid(), (ushort)percent),
                "Walk speed " + percent + "%");
        }

        // Asks the worker to move the character onto the ground at a point.
        public bool RequestTeleport(Vector3 point)
        {
            return Send(
                RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), point.x, point.y + TeleportLift, point.z),
                "Teleport to " + point.x.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + point.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + point.z.ToString("0.0", CultureInfo.InvariantCulture));
        }

        public void AddWorldMenuOptions(WorldMenuBuilder menu)
        {
            if (IsOpen && ToolsAvailable)
            {
                menu.Add(WorldMenuOption.ForGround(this, TeleportAction, TeleportVerb, menu.Ray, IsPickingTeleport));
            }
        }

        public bool ExecuteWorldMenuOption(WorldMenuOption option)
        {
            if (option.Action != TeleportAction || !IsOpen || !ToolsAvailable)
            {
                return false;
            }

            SetPicking(false);
            FindLocalPlayer();
            if (localPlayer == null || !localPlayer.TryFindGroundPoint(option.Ray, out var point))
            {
                ShowResult("There is no ground there to teleport to.", TemporaryUiKit.WarningColor);
                return false;
            }

            return RequestTeleport(point);
        }

        private void Start()
        {
            BuildUi();
            SetOpen(false);
            if (simulationClient != null)
            {
                simulationClient.AdminStateChanged += RefreshAll;
                simulationClient.AdminIntentCompleted += OnAdminIntentCompleted;
                simulationClient.SessionJoined += RefreshAll;
            }

            if (bodyLook != null)
            {
                bodyLook.Changed += RefreshAll;
            }

            if (combat != null)
            {
                combat.State.Changed += RefreshHealth;
            }

            worldMenu?.AddProvider(this, WorldMenuOrder.Admin);
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.AdminStateChanged -= RefreshAll;
                simulationClient.AdminIntentCompleted -= OnAdminIntentCompleted;
                simulationClient.SessionJoined -= RefreshAll;
            }

            if (bodyLook != null)
            {
                bodyLook.Changed -= RefreshAll;
            }

            if (combat != null)
            {
                combat.State.Changed -= RefreshHealth;
            }

            worldMenu?.RemoveProvider(this);
            window?.Destroy();
        }

        private void Update()
        {
            FindLocalPlayer();
            var input = localPlayer != null ? localPlayer.PlayerInput : null;
            if (input == null)
            {
                return;
            }

            if (input.ToggleAdminMenuPressedThisFrame)
            {
                SetOpen(!IsOpen);
            }
            else if (IsPickingTeleport
                && input.CancelPressedThisFrame
                && (worldMenu == null || !worldMenu.CancelConsumedThisFrame))
            {
                SetPicking(false);
                ShowResult("Teleport cancelled.", TemporaryUiKit.TextSecondary);
            }

            if (ToolsAvailable != lastAvailable)
            {
                RefreshAll();
            }
        }

        private void BuildUi()
        {
            window = TemporaryMenuWindow.Create(
                transform,
                WindowName,
                "Admin",
                "F3  Development only",
                new Vector2(0f, 1f),
                new Vector2(16f, -136f),
                new Vector2(430f, 500f),
                false,
                () => SetOpen(false));

            lookPage = window.AddTab("Look");
            toolPages.Add(lookPage.gameObject.AddComponent<CanvasGroup>());

            var movement = window.AddTab("Movement");
            toolPages.Add(movement.gameObject.AddComponent<CanvasGroup>());
            BuildMovementTab(movement);

            var combatPage = window.AddTab("Combat");
            toolPages.Add(combatPage.gameObject.AddComponent<CanvasGroup>());
            BuildCombatTab(combatPage);
        }

        private void BuildMovementTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Walk speed");
            speedValue = TemporaryMenuWindow.AddValue(page, "Now");
            var speeds = TemporaryMenuWindow.AddButtonRow(page);
            foreach (var percent in SpeedChoices)
            {
                var chosen = percent;
                speedButtons.Add(TemporaryUiKit.CreateButton(
                    speeds,
                    (percent / 100f).ToString("0.#", CultureInfo.InvariantCulture) + "x",
                    () => RequestSpeed(chosen),
                    true,
                    TemporaryButtonTone.Normal));
            }

            TemporaryMenuWindow.AddNote(page, "The worker walks you at this speed until you leave the World.");

            TemporaryMenuWindow.AddSection(page, "Teleport");
            var pickRow = TemporaryMenuWindow.AddButtonRow(page);
            pickButton = TemporaryUiKit.CreateButton(
                pickRow,
                "Pick a spot",
                () => SetPicking(!IsPickingTeleport),
                true,
                TemporaryButtonTone.Normal);
            pickLabel = pickButton.GetComponentInChildren<Text>();
            TemporaryMenuWindow.AddNote(
                page,
                "Then left-click the ground; Escape cancels. While this menu is open, right-clicking the "
                + "ground offers " + TeleportVerb + " too.");
        }

        private void BuildCombatTab(RectTransform page)
        {
            TemporaryMenuWindow.AddSection(page, "Health and mana");
            healthValue = TemporaryMenuWindow.AddValue(page, "Now");
            var row = TemporaryMenuWindow.AddButtonRow(page);
            TemporaryUiKit.CreateButton(
                row,
                "Restore health and mana",
                () => Send(RealtimeAdminIntent.CreateRestoreHealth(Guid.NewGuid()), "Restore health and mana"),
                true,
                TemporaryButtonTone.Normal);
            invulnerableToggle = TemporaryMenuWindow.AddToggle(
                page,
                "Invulnerable: hits leave your health as it is",
                false,
                value => Send(
                    RealtimeAdminIntent.CreateSetInvulnerable(Guid.NewGuid(), value),
                    value ? "Invulnerable on" : "Invulnerable off"));
            TemporaryMenuWindow.AddNote(page, "Invulnerability lasts until you leave the World.");
        }

        private void SetPicking(bool picking)
        {
            IsPickingTeleport = picking && ToolsAvailable;
            if (pickButton == null)
            {
                return;
            }

            TemporaryUiKit.SetButtonTone(
                pickButton,
                IsPickingTeleport ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            pickLabel.text = IsPickingTeleport ? "Click the ground..." : "Pick a spot";
            if (IsPickingTeleport)
            {
                ShowResult("Left-click the ground to teleport there; Escape cancels.", TemporaryUiKit.AccentColor);
            }
        }

        private bool Send(RealtimeAdminIntent intent, string description)
        {
            if (!ToolsAvailable)
            {
                RefreshAll();
                return false;
            }

            if (!simulationClient.TrySendAdminIntent(intent))
            {
                ShowResult(description + " could not be sent.", TemporaryUiKit.DangerColor);
                return false;
            }

            pendingDescriptions[intent.OperationId] = description;
            ShowResult(description + "...", TemporaryUiKit.TextSecondary);
            return true;
        }

        private void OnAdminIntentCompleted(RealtimeAdminIntentResult result)
        {
            if (!pendingDescriptions.TryGetValue(result.OperationId, out var description))
            {
                return;
            }

            pendingDescriptions.Remove(result.OperationId);
            if (result.Succeeded)
            {
                ShowResult(description + ": done.", TemporaryUiKit.SuccessColor);
                return;
            }

            ShowResult(description + " refused: " + result.Error.Message, TemporaryUiKit.DangerColor);
            RefreshAll();
        }

        private void ShowResult(string text, Color color)
        {
            resultText = text;
            resultColor = color;
            window?.SetStatus(text, color);
        }

        private void RefreshAll()
        {
            if (window == null)
            {
                return;
            }

            var available = ToolsAvailable;
            lastAvailable = available;
            foreach (var page in toolPages)
            {
                page.interactable = available;
                page.alpha = available ? 1f : 0.55f;
            }

            if (!available)
            {
                SetPicking(false);
                pendingDescriptions.Clear();
                window.SetStatus(
                    simulationClient != null && simulationClient.IsJoined
                        ? "The admin tools are off: only a SimulationWorker in the Development environment allows them."
                        : "Join a World to use the admin tools.",
                    TemporaryUiKit.WarningColor);
            }
            else if (resultText != null)
            {
                window.SetStatus(resultText, resultColor);
            }
            else
            {
                window.SetStatus("The admin tools are on.", TemporaryUiKit.TextSecondary);
            }

            RefreshSpeed();
            RefreshHealth();
            RefreshLookRows();
        }

        private void RefreshSpeed()
        {
            var state = ToolsAvailable ? simulationClient.AdminState : null;
            var percent = state != null ? state.SpeedPercent : 100;
            var session = simulationClient != null ? simulationClient.MovementSession : null;
            speedValue.text = percent + "%"
                + (session != null
                    ? " (" + session.Settings.WithWalkSpeedPercent(percent).WalkSpeed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s)"
                    : string.Empty);
            for (var index = 0; index < speedButtons.Count; index++)
            {
                TemporaryUiKit.SetButtonTone(
                    speedButtons[index],
                    SpeedChoices[index] == percent ? TemporaryButtonTone.Active : TemporaryButtonTone.Normal);
            }

            invulnerableToggle.SetIsOnWithoutNotify(state != null && state.Invulnerable);
        }

        private void RefreshHealth()
        {
            if (healthValue == null)
            {
                return;
            }

            var owner = combat != null ? combat.State.Owner : null;
            healthValue.text = owner == null
                ? "-"
                : owner.Health + " / " + owner.MaximumHealth
                    + ", mana " + owner.Mana + " / " + owner.MaximumMana
                    + (owner.LifeState == RealtimeCombatLifeState.Dead ? " (dead)" : string.Empty);
        }

        // The rows that step through the look of the local body: its body model, every appearance
        // slot, every category of its paint layers, and its hair. They are built again when the
        // look, the body model, or the catalog changes.
        private void RefreshLookRows()
        {
            if (lookPage == null)
            {
                return;
            }

            var received = bodyLook != null && bodyLook.Received;
            if (received == rowsReceived
                && (!received
                    || (ReferenceEquals(rowsLook, bodyLook.Look)
                        && rowsModel == bodyLook.Model
                        && rowsCatalog == bodyLook.Catalog)))
            {
                return;
            }

            rowsReceived = received;
            rowsLook = received ? bodyLook.Look : null;
            rowsModel = received ? bodyLook.Model : null;
            rowsCatalog = received ? bodyLook.Catalog : null;
            for (var index = lookPage.childCount - 1; index >= 0; index--)
            {
                Destroy(lookPage.GetChild(index).gameObject);
            }

            TemporaryMenuWindow.AddSection(lookPage, "Your look");
            TemporaryMenuWindow.AddNote(
                lookPage,
                received
                    ? "The server stores your look. Creation: chosen when a character is made; in game: may also change while playing."
                    : "Your look shows once you joined a World.");
            if (!received)
            {
                return;
            }

            AddBodyModelRow();
            AddLookRows();
        }

        private void AddBodyModelRow()
        {
            var models = bodyLook.Models;
            if (models == null || models.Models.Count < 2)
            {
                return;
            }

            var shown = models.NamedOrDefault(bodyLook.Model);
            var current = 0;
            for (var index = 0; index < models.Models.Count; index++)
            {
                if (models.Models[index] == shown)
                {
                    current = index;
                }
            }

            AddLookRow(
                "Body",
                AppearanceRules.BodyModel,
                shown != null ? shown.Name : "none",
                step =>
                {
                    var chosen = models.Models[(current + step + models.Models.Count) % models.Models.Count];
                    if (chosen != null)
                    {
                        bodyLook.RequestDevelopmentLook(chosen.Name, bodyLook.Look);
                    }
                });
        }

        private void AddLookRows()
        {
            var catalog = bodyLook.Catalog;
            if (catalog == null)
            {
                return;
            }

            foreach (var slot in catalog.Slots)
            {
                if (slot == null || slot.Options.Count < 2)
                {
                    continue;
                }

                var options = slot.Options;
                var current = 0;
                for (var index = 0; index < options.Count; index++)
                {
                    if (string.Equals(options[index].Name, bodyLook.Look.OptionFor(slot.Name), StringComparison.Ordinal))
                    {
                        current = index;
                    }
                }

                var category = AppearanceRules.OfSlot(slot.Name);
                AddLookRow(
                    slot.Name,
                    category.HasValue ? AppearanceRules.For(category.Value) : (AppearanceRules.Rule?)null,
                    options[current].Name,
                    step => bodyLook.RequestDevelopmentLook(
                        null,
                        bodyLook.Look.With(slot.Name, options[(current + step + options.Count) % options.Count].Name)));
            }

            // Paint layers show on the body without being equipped: one row per category and slot,
            // with no paint among the choices of a category that a body may go without.
            var shown = new List<BodyAppearanceCatalog.Paint>();
            catalog.ShownPaints(bodyLook.Look, null, shown);
            var groups = catalog.Paints.Where(paint => paint != null)
                .GroupBy(paint => (paint.Category, paint.Slot))
                .ToArray();
            foreach (var group in groups)
            {
                var rule = AppearanceRules.For(group.Key.Category);
                var choices = new List<BodyAppearanceCatalog.Paint>();
                if (!rule.Required)
                {
                    choices.Add(null);
                }

                choices.AddRange(group);
                var current = Math.Max(choices.FindIndex(choice => choice != null && shown.Contains(choice)), 0);
                var name = group.Key.Category + (group.Key.Slot.Length > 0 ? " (" + group.Key.Slot + ")" : string.Empty);
                AddLookRow(
                    name,
                    rule,
                    choices[current] != null ? choices[current].Name : "none",
                    step =>
                    {
                        if (choices.Count < 2)
                        {
                            return;
                        }

                        var look = bodyLook.Look;
                        foreach (var paint in group)
                        {
                            look = look.WithPaint(paint.Name, false);
                        }

                        var chosen = choices[(current + step + choices.Count) % choices.Count];
                        bodyLook.RequestDevelopmentLook(null, chosen != null ? look.WithPaint(chosen.Name, true) : look);
                    });
            }

            // The hair is part of the look, a garment of the hair slot or none; every other garment
            // comes from the equipped items.
            var hairs = new List<BodyAppearanceCatalog.Garment> { null };
            hairs.AddRange(catalog.Garments.Where(garment => garment != null && garment.Slot == BodyAppearanceCatalog.Garment.HairSlot));
            if (hairs.Count > 1)
            {
                var current = Math.Max(hairs.FindIndex(hair => hair != null && bodyLook.Look.HasGarment(hair.Name)), 0);
                AddLookRow(
                    "Hair",
                    AppearanceRules.Hair,
                    hairs[current] != null ? hairs[current].Name : "none",
                    step =>
                    {
                        var look = bodyLook.Look.WithoutGarments();
                        var chosen = hairs[(current + step + hairs.Count) % hairs.Count];
                        bodyLook.RequestDevelopmentLook(null, chosen != null ? look.WithGarment(chosen.Name, true) : look);
                    });
            }
        }

        private void AddLookRow(string kind, AppearanceRules.Rule? rule, string value, Action<int> step)
        {
            var label = kind;
            if (rule.HasValue)
            {
                label += "  <size=11><color=" + TemporaryUiKit.ColorTag(TemporaryUiKit.TextMuted) + ">"
                    + (rule.Value.ChangesInGame ? "in game" : "creation") + "</color></size>";
            }

            TemporaryMenuWindow.AddStepper(lookPage, label, step).text = value;
        }

        private void FindLocalPlayer()
        {
            if (localPlayer == null)
            {
                localPlayer = FindAnyObjectByType<LocalPlayerController>();
            }
        }
    }
}
