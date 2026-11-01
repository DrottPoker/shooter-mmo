using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // Replaceable runtime uGUI combat presentation: the owner's health, live arrow count,
    // and attack status, a frame for the attack target, health bars over damaged
    // players in view, floating damage numbers, and a death overlay with the respawn
    // countdown. Mobs show their health on their nameplates instead, see WorldNameplates.
    // It reads the combat state and never decides range, damage, or death.
    // Nothing on it takes a raycast, so it never blocks a world click. It places health bars
    // and damage numbers after the camera moved, so they stay on the heads they belong to.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(PresentationExecutionOrder.ScreenAnchoredInterface)]
    public sealed class TemporaryCombatHud : MonoBehaviour
    {
        public const string OwnerFrameName = "CombatOwnerFrame";
        public const string TargetFrameName = "CombatTargetFrame";
        public const string DeathOverlayName = "CombatDeathOverlay";

        // The HUD draws below the interaction panel and the inventory.
        public const int CanvasSortingOrder = 20;

        // The death overlay draws above every panel, so an open inventory or corpse view
        // never hides a death and its countdown.
        public const int DeathOverlaySortingOrder = TemporaryInventoryPanel.CanvasSortingOrder + 50;

        private const int MaximumHealthBars = 32;
        private const int MaximumDamageNumbers = 24;
        private const float HealthBarRange = 60f;
        private const float HealthBarLift = 0.35f;
        private static readonly Vector2 HealthBarSize = new Vector2(72f, 9f);
        private const float DamageNumberSeconds = 1.1f;
        private const float DamageNumberRise = 56f;
        private const float RejectionSeconds = 4f;
        private static readonly Color BarTrackColor = new Color(0f, 0f, 0f, 0.7f);

        private readonly List<ulong> damagedCombatants = new List<ulong>();
        private readonly List<HealthBar> healthBars = new List<HealthBar>();
        private readonly List<DamageNumber> damageNumbers = new List<DamageNumber>();
        private readonly Stack<TextMeshProUGUI> idleDamageTexts = new Stack<TextMeshProUGUI>();
        private CombatClientController combatController;
        private CombatTargetingController targetingController;
        private RealtimeSimulationClient realtimeClient;
        private CombatantViewLocator views;
        private RectTransform worldLayer;
        private GameObject ownerFrame;
        private Bar ownerHealth;
        private TextMeshProUGUI ownerHealthText;
        private Bar ownerMana;
        private TextMeshProUGUI ownerManaText;
        private TextMeshProUGUI ownerArrowText;
        private TextMeshProUGUI ownerStatusText;
        private TextMeshProUGUI messageText;
        private GameObject targetFrame;
        private TextMeshProUGUI targetNameText;
        private Bar targetHealth;
        private TextMeshProUGUI targetDetailText;
        private GameObject deathOverlay;
        private TextMeshProUGUI deathCountdownText;
        private float messageVisibleUntil = -1f;
        private ulong shownTargetEntityId;
        private string shownTargetName = string.Empty;
        private OwnerLines shownOwner;
        private TargetLines shownTarget;
        private int shownCountdownSeconds = -1;
        private bool subscribed;

        public bool IsOwnerFrameVisible
        {
            get { return ownerFrame != null && ownerFrame.activeSelf; }
        }

        public bool IsDeathOverlayVisible
        {
            get { return deathOverlay != null && deathOverlay.activeSelf; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            CombatClientController combat,
            CombatTargetingController targeting,
            RealtimeSimulationClient realtime,
            CombatantViewLocator combatantViews)
        {
            combatController = combat;
            targetingController = targeting;
            realtimeClient = realtime;
            views = combatantViews;
        }

        private void Start()
        {
            BuildUi();
            if (combatController != null)
            {
                combatController.State.Changed += OnCombatStateChanged;
                combatController.State.EventsReceived += OnEventsReceived;
                combatController.State.IntentCompleted += OnIntentCompleted;
                subscribed = true;
            }

            OnCombatStateChanged();
            LateUpdate();
        }

        private void OnDestroy()
        {
            if (subscribed && combatController != null)
            {
                combatController.State.Changed -= OnCombatStateChanged;
                combatController.State.EventsReceived -= OnEventsReceived;
                combatController.State.IntentCompleted -= OnIntentCompleted;
            }

            subscribed = false;
        }

        private void LateUpdate()
        {
            if (ownerFrame == null)
            {
                return;
            }

            var owner = combatController != null ? combatController.State.Owner : null;
            var joined = owner != null && views != null && views.LocalPlayer != null;
            ownerFrame.SetActive(joined);
            if (!joined)
            {
                targetFrame.SetActive(false);
                deathOverlay.SetActive(false);
                messageText.gameObject.SetActive(false);
                HideFrom(healthBars, 0);
                HideNumbers();
                return;
            }

            UpdateOwnerFrame(owner);
            UpdateTargetFrame();
            UpdateDeathOverlay(owner);
            UpdateHealthBars(owner);
            UpdateDamageNumbers();
            messageText.gameObject.SetActive(Time.unscaledTime < messageVisibleUntil);
        }

        // Text only changes when the values do, so a frame allocates no strings.
        private void UpdateOwnerFrame(RealtimeOwnerCombatState owner)
        {
            var lines = new OwnerLines(owner);
            if (lines.Equals(shownOwner) && ownerHealthText.text.Length > 0)
            {
                return;
            }

            shownOwner = lines;
            var fraction = CombatHudPresentation.HealthFraction(owner.Health, owner.MaximumHealth);
            ownerHealth.Set(fraction, CombatHudPresentation.HealthColor(fraction));
            ownerHealthText.text = "Health " + CombatHudPresentation.HealthText(
                owner.Health,
                owner.MaximumHealth);
            ownerMana.Set(
                CombatHudPresentation.HealthFraction(owner.Mana, owner.MaximumMana),
                CombatHudPresentation.ManaColor);
            ownerManaText.text = CombatHudPresentation.ManaText(owner.Mana, owner.MaximumMana);
            ownerArrowText.text = CombatHudPresentation.ArrowCountText(
                lines.HasAmmunition,
                owner.AmmunitionQuantity);
            ownerStatusText.text = CombatHudPresentation.AttackStatusLabel(owner.AttackStatus)
                + (owner.InCombat ? "    In combat" : string.Empty);
        }

        private void UpdateTargetFrame()
        {
            var targetId = targetingController != null ? targetingController.TargetEntityId : 0;
            if (targetId == 0
                || !combatController.State.TryGetCombatant(targetId, out var state))
            {
                targetFrame.SetActive(false);
                return;
            }

            if (targetId != shownTargetEntityId)
            {
                shownTargetEntityId = targetId;
                shownTargetName = string.Empty;
            }

            if (views.TryGetView(targetId, out var view) && view.DisplayName.Length > 0)
            {
                shownTargetName = view.DisplayName;
            }

            targetFrame.SetActive(true);
            var lines = new TargetLines(targetId, shownTargetName, state);
            if (lines.Equals(shownTarget))
            {
                return;
            }

            shownTarget = lines;
            targetNameText.text = shownTargetName.Length > 0 ? shownTargetName : "Target";
            var fraction = CombatHudPresentation.HealthFraction(state.Health, state.MaximumHealth);
            targetHealth.Set(fraction, CombatHudPresentation.HealthColor(fraction));
            var life = state.LifeState == RealtimeCombatLifeState.Evading
                ? "    Returning home"
                : state.LifeState == RealtimeCombatLifeState.Dead
                    ? "    Dead"
                    : state.InCombat ? "    In combat" : string.Empty;
            targetDetailText.text = CombatHudPresentation.HealthText(state.Health, state.MaximumHealth)
                + life;
        }

        private void UpdateDeathOverlay(RealtimeOwnerCombatState owner)
        {
            var dead = owner.LifeState == RealtimeCombatLifeState.Dead;
            deathOverlay.SetActive(dead);
            if (!dead)
            {
                return;
            }

            var clock = CombatServerClock.FromClient(realtimeClient);
            var seconds = clock.IsKnown
                ? Mathf.CeilToInt(Mathf.Max(0f, clock.SecondsUntil(owner.RespawnTick)))
                : -1;
            if (seconds == shownCountdownSeconds && deathCountdownText.text.Length > 0)
            {
                return;
            }

            shownCountdownSeconds = seconds;
            deathCountdownText.text = CombatHudPresentation.RespawnCountdownText(
                clock.IsKnown,
                clock.SecondsUntil(owner.RespawnTick));
        }

        private void UpdateHealthBars(RealtimeOwnerCombatState owner)
        {
            var playerCamera = views.PlayerCamera;
            var used = 0;
            if (playerCamera != null)
            {
                var ownerId = views.ControlledEntityId;
                if (CombatHudPresentation.ShowsHealthBar(owner.Health, owner.MaximumHealth, owner.LifeState))
                {
                    used = PlaceHealthBar(playerCamera, ownerId, owner.Health, owner.MaximumHealth, used);
                }

                for (var index = 0; index < damagedCombatants.Count && used < MaximumHealthBars; index++)
                {
                    var entityId = damagedCombatants[index];
                    if (entityId != ownerId
                        && !views.IsMob(entityId)
                        && combatController.State.TryGetCombatant(entityId, out var state)
                        && CombatHudPresentation.ShowsHealthBar(state.Health, state.MaximumHealth, state.LifeState))
                    {
                        used = PlaceHealthBar(playerCamera, entityId, state.Health, state.MaximumHealth, used);
                    }
                }
            }

            HideFrom(healthBars, used);
        }

        private int PlaceHealthBar(Camera playerCamera, ulong entityId, int health, int maximumHealth, int used)
        {
            if (!views.TryGetView(entityId, out var view)
                || (view.Position - playerCamera.transform.position).sqrMagnitude
                    > HealthBarRange * HealthBarRange
                || !TryProject(playerCamera, view.Top + (Vector3.up * HealthBarLift), out var local))
            {
                return used;
            }

            if (used == healthBars.Count)
            {
                healthBars.Add(CreateHealthBar());
            }

            var bar = healthBars[used];
            bar.Root.gameObject.SetActive(true);
            bar.Root.anchoredPosition = local;
            var fraction = CombatHudPresentation.HealthFraction(health, maximumHealth);
            bar.Bar.Set(fraction, CombatHudPresentation.HealthColor(fraction));
            return used + 1;
        }

        // A Mob's damage numbers rise from above its nameplate, at the nameplate's size, so they
        // never cover its health.
        private void UpdateDamageNumbers()
        {
            var playerCamera = views.PlayerCamera;
            var player = views.LocalPlayer;
            for (var index = damageNumbers.Count - 1; index >= 0; index--)
            {
                var number = damageNumbers[index];
                var age = Time.unscaledTime - number.StartedAt;
                if (age >= DamageNumberSeconds || number.Text == null)
                {
                    damageNumbers.RemoveAt(index);
                    ReleaseDamageText(number.Text);
                    continue;
                }

                if (views.TryGetView(number.TargetEntityId, out var view))
                {
                    number.Anchor = view.Top;
                    number.NameplateScale = player != null
                        ? NameplatePresentation.MobNameplateScale(
                            Vector3.Distance(view.Position, player.transform.position))
                        : 1f;
                    damageNumbers[index] = number;
                }

                var overNameplate = views.IsMob(number.TargetEntityId);
                var anchor = overNameplate
                    ? number.Anchor + (Vector3.up * WorldNameplates.AnchorLift)
                    : number.Anchor;
                if (playerCamera == null || !TryProject(playerCamera, anchor, out var local))
                {
                    number.Text.gameObject.SetActive(false);
                    continue;
                }

                var progress = age / DamageNumberSeconds;
                var start = overNameplate ? WorldNameplates.Height * number.NameplateScale : 0f;
                number.Text.gameObject.SetActive(true);
                number.Text.rectTransform.anchoredPosition = local + new Vector2(0f, start + (DamageNumberRise * progress));
                var color = number.Color;
                color.a = 1f - (progress * progress);
                number.Text.color = color;
            }
        }

        private void OnCombatStateChanged()
        {
            damagedCombatants.Clear();
            if (combatController == null)
            {
                return;
            }

            var combatants = combatController.State.Combatants;
            for (var index = 0; index < combatants.Count; index++)
            {
                var state = combatants[index];
                if (CombatHudPresentation.ShowsHealthBar(state.Health, state.MaximumHealth, state.LifeState))
                {
                    damagedCombatants.Add(state.EntityId);
                }
            }
        }

        private void OnEventsReceived(RealtimeCombatEventBatch batch)
        {
            if (worldLayer == null || views == null || batch.Events == null)
            {
                return;
            }

            for (var index = 0; index < batch.Events.Length; index++)
            {
                var combatEvent = batch.Events[index];
                if (combatEvent.Kind != RealtimeCombatEventKind.Hit
                    || !views.TryGetView(combatEvent.TargetEntityId, out var view))
                {
                    continue;
                }

                ShowDamageNumber(
                    combatEvent.TargetEntityId,
                    view.Top,
                    combatEvent.Damage,
                    CombatHudPresentation.DamageColor(combatEvent.DamageType));
            }
        }

        private void OnIntentCompleted(RealtimeCombatIntentResult result)
        {
            if (result.Succeeded || result.Error == null || messageText == null)
            {
                return;
            }

            messageText.text = result.Error.Message;
            messageVisibleUntil = Time.unscaledTime + RejectionSeconds;
        }

        // The oldest number makes room when too many fly at once.
        private void ShowDamageNumber(ulong targetEntityId, Vector3 anchor, int damage, Color color)
        {
            if (damageNumbers.Count >= MaximumDamageNumbers)
            {
                var oldest = damageNumbers[0];
                damageNumbers.RemoveAt(0);
                ReleaseDamageText(oldest.Text);
            }

            TextMeshProUGUI text = null;
            while (text == null && idleDamageTexts.Count > 0)
            {
                text = idleDamageTexts.Pop();
            }

            if (text == null)
            {
                text = CreateDamageText();
            }

            text.text = damage.ToString(CultureInfo.InvariantCulture);
            text.color = color;
            text.gameObject.SetActive(false);
            damageNumbers.Add(new DamageNumber(text, targetEntityId, anchor, color, Time.unscaledTime));
        }

        private void ReleaseDamageText(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }

            text.gameObject.SetActive(false);
            idleDamageTexts.Push(text);
        }

        private bool TryProject(Camera playerCamera, Vector3 worldPosition, out Vector2 local)
        {
            local = Vector2.zero;
            var screen = playerCamera.WorldToScreenPoint(worldPosition);
            return screen.z > 0f
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    worldLayer,
                    screen,
                    null,
                    out local);
        }

        private void HideNumbers()
        {
            for (var index = 0; index < damageNumbers.Count; index++)
            {
                ReleaseDamageText(damageNumbers[index].Text);
            }

            damageNumbers.Clear();
        }

        private static void HideFrom(List<HealthBar> bars, int firstHidden)
        {
            for (var index = firstHidden; index < bars.Count; index++)
            {
                if (bars[index].Root.gameObject.activeSelf)
                {
                    bars[index].Root.gameObject.SetActive(false);
                }
            }
        }

        private void BuildUi()
        {
            var canvasObject = new GameObject(
                "TemporaryCombatCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = CanvasSortingOrder;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            worldLayer = TemporaryUiKit.CreateUiObject("CombatWorldLayer", canvasObject.transform)
                .GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(worldLayer, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            BuildOwnerFrame(canvasObject.transform);
            BuildTargetFrame(canvasObject.transform);
            BuildDeathOverlay(canvasObject.transform);
        }

        private void BuildOwnerFrame(Transform parent)
        {
            var frame = CreateFrame(parent, OwnerFrameName, new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(330f, 144f));
            ownerFrame = frame.gameObject;
            ownerHealthText = CreateLine(frame, 10f, 18, FontStyles.Bold, TemporaryUiKit.TextPrimary);
            ownerHealth = CreateBar(frame, new Vector2(14f, -36f), new Vector2(302f, 12f));
            ownerManaText = CreateLine(frame, 52f, 15, FontStyles.Bold, TemporaryUiKit.TextPrimary);
            ownerMana = CreateBar(frame, new Vector2(14f, -76f), new Vector2(302f, 10f));
            ownerArrowText = CreateLine(frame, 94f, 15, FontStyles.Normal, TemporaryUiKit.TextSecondary);
            ownerStatusText = CreateLine(frame, 116f, 15, FontStyles.Normal, TemporaryUiKit.TextSecondary);

            messageText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("CombatMessage", parent),
                string.Empty,
                16,
                FontStyles.Bold,
                TextAlignmentOptions.TopLeft,
                TemporaryUiKit.WarningColor);
            var messageRect = messageText.rectTransform;
            messageRect.anchorMin = new Vector2(0f, 1f);
            messageRect.anchorMax = new Vector2(0f, 1f);
            messageRect.pivot = new Vector2(0f, 1f);
            messageRect.anchoredPosition = new Vector2(18f, -166f);
            messageRect.sizeDelta = new Vector2(600f, 24f);
            OutlineText(messageText);
            messageText.gameObject.SetActive(false);
        }

        private void BuildTargetFrame(Transform parent)
        {
            var frame = CreateFrame(parent, TargetFrameName, new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(360f, 84f));
            targetFrame = frame.gameObject;
            targetNameText = CreateLine(frame, 8f, 18, FontStyles.Bold, TemporaryUiKit.TextPrimary);
            targetHealth = CreateBar(frame, new Vector2(14f, -36f), new Vector2(332f, 12f));
            targetDetailText = CreateLine(frame, 54f, 15, FontStyles.Normal, TemporaryUiKit.TextSecondary);
            targetFrame.SetActive(false);
        }

        private void BuildDeathOverlay(Transform parent)
        {
            deathOverlay = TemporaryUiKit.CreateUiObject(DeathOverlayName, parent);
            var overlayCanvas = deathOverlay.AddComponent<Canvas>();
            overlayCanvas.overrideSorting = true;
            overlayCanvas.sortingOrder = DeathOverlaySortingOrder;
            TemporaryUiKit.Stretch(
                deathOverlay.GetComponent<RectTransform>(),
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            var dim = deathOverlay.AddComponent<Image>();
            dim.color = new Color(0.08f, 0f, 0f, 0.55f);
            dim.raycastTarget = false;

            var title = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Title", deathOverlay.transform),
                "You died",
                54,
                FontStyles.Bold,
                TextAlignmentOptions.Center,
                TemporaryUiKit.DangerColor);
            TemporaryUiKit.Stretch(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 10f), new Vector2(0f, 80f));
            OutlineText(title);

            deathCountdownText = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Countdown", deathOverlay.transform),
                string.Empty,
                26,
                FontStyles.Bold,
                TextAlignmentOptions.Center,
                TemporaryUiKit.TextPrimary);
            TemporaryUiKit.Stretch(deathCountdownText.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, -50f), new Vector2(0f, 0f));
            OutlineText(deathCountdownText);
            deathOverlay.SetActive(false);
        }

        private static RectTransform CreateFrame(
            Transform parent,
            string name,
            Vector2 anchor,
            Vector2 position,
            Vector2 size)
        {
            var frame = TemporaryUiKit.CreateUiObject(name, parent).GetComponent<RectTransform>();
            frame.anchorMin = anchor;
            frame.anchorMax = anchor;
            frame.pivot = anchor;
            frame.anchoredPosition = position;
            frame.sizeDelta = size;
            var background = frame.gameObject.AddComponent<Image>();
            background.color = TemporaryUiKit.PanelColor;
            background.raycastTarget = false;
            TemporaryUiKit.AddOutline(background, TemporaryUiKit.PanelBorderColor);
            return frame;
        }

        private static TextMeshProUGUI CreateLine(RectTransform frame, float top, int fontSize, FontStyles style, Color color)
        {
            var text = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Line", frame),
                string.Empty,
                fontSize,
                style,
                TextAlignmentOptions.TopLeft,
                color);
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(14f, -top - (fontSize + 8f));
            rect.offsetMax = new Vector2(-14f, -top);
            return text;
        }

        private static Bar CreateBar(RectTransform parent, Vector2 topLeft, Vector2 size)
        {
            var track = TemporaryUiKit.CreateUiObject("HealthBar", parent).GetComponent<RectTransform>();
            track.anchorMin = new Vector2(0f, 1f);
            track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0f, 1f);
            track.anchoredPosition = topLeft;
            track.sizeDelta = size;
            return CreateBarFill(track);
        }

        private static Bar CreateBarFill(RectTransform track)
        {
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = BarTrackColor;
            trackImage.raycastTarget = false;
            var fill = TemporaryUiKit.CreateUiObject("Fill", track).AddComponent<Image>();
            fill.raycastTarget = false;
            TemporaryUiKit.Stretch(fill.rectTransform, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));
            return new Bar(fill);
        }

        private HealthBar CreateHealthBar()
        {
            var root = TemporaryUiKit.CreateUiObject("WorldHealthBar", worldLayer).GetComponent<RectTransform>();
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0f);
            root.sizeDelta = HealthBarSize;
            // Bars stay behind damage numbers.
            root.SetAsFirstSibling();
            return new HealthBar(root, CreateBarFill(root));
        }

        // A damage number is a TextMesh Pro world label, like the nameplates.
        private TextMeshProUGUI CreateDamageText()
        {
            var text = WorldLabels.Create(
                worldLayer,
                "DamageNumber",
                26f,
                HorizontalAlignmentOptions.Center,
                VerticalAlignmentOptions.Middle,
                CombatHudPresentation.NormalDamageColor);
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(120f, 34f);
            text.gameObject.SetActive(false);
            return text;
        }

        // Text over the world reads on any background with the world labels' black outline.
        private static void OutlineText(TextMeshProUGUI text)
        {
            text.fontSharedMaterial = WorldLabels.OutlinedMaterial;
        }

        private readonly struct Bar
        {
            public Bar(Image fill)
            {
                Fill = fill;
            }

            public Image Fill { get; }

            public void Set(float fraction, Color color)
            {
                Fill.rectTransform.anchorMax = new Vector2(fraction, 1f);
                Fill.color = color;
            }
        }

        private readonly struct HealthBar
        {
            public HealthBar(RectTransform root, Bar bar)
            {
                Root = root;
                Bar = bar;
            }

            public RectTransform Root { get; }

            public Bar Bar { get; }
        }

        // The owner values the frame shows.
        private readonly struct OwnerLines : IEquatable<OwnerLines>
        {
            public OwnerLines(RealtimeOwnerCombatState owner)
            {
                Health = owner.Health;
                MaximumHealth = owner.MaximumHealth;
                Mana = owner.Mana;
                MaximumMana = owner.MaximumMana;
                HasAmmunition = owner.AmmunitionItemInstanceId != Guid.Empty;
                AmmunitionQuantity = owner.AmmunitionQuantity;
                Status = owner.AttackStatus;
                InCombat = owner.InCombat;
            }

            public int Health { get; }

            public int MaximumHealth { get; }

            public int Mana { get; }

            public int MaximumMana { get; }

            public bool HasAmmunition { get; }

            public int AmmunitionQuantity { get; }

            public RealtimeCombatAttackStatus Status { get; }

            public bool InCombat { get; }

            public bool Equals(OwnerLines other)
            {
                return Health == other.Health
                    && MaximumHealth == other.MaximumHealth
                    && Mana == other.Mana
                    && MaximumMana == other.MaximumMana
                    && HasAmmunition == other.HasAmmunition
                    && AmmunitionQuantity == other.AmmunitionQuantity
                    && Status == other.Status
                    && InCombat == other.InCombat;
            }
        }

        // The target values the frame shows.
        private readonly struct TargetLines : IEquatable<TargetLines>
        {
            public TargetLines(ulong entityId, string name, RealtimeCombatantState state)
            {
                EntityId = entityId;
                Name = name;
                Health = state.Health;
                MaximumHealth = state.MaximumHealth;
                LifeState = state.LifeState;
                InCombat = state.InCombat;
            }

            public ulong EntityId { get; }

            public string Name { get; }

            public int Health { get; }

            public int MaximumHealth { get; }

            public RealtimeCombatLifeState LifeState { get; }

            public bool InCombat { get; }

            public bool Equals(TargetLines other)
            {
                return EntityId == other.EntityId
                    && string.Equals(Name, other.Name, StringComparison.Ordinal)
                    && Health == other.Health
                    && MaximumHealth == other.MaximumHealth
                    && LifeState == other.LifeState
                    && InCombat == other.InCombat;
            }
        }

        private struct DamageNumber
        {
            public DamageNumber(
                TextMeshProUGUI text,
                ulong targetEntityId,
                Vector3 anchor,
                Color color,
                float startedAt)
            {
                Text = text;
                TargetEntityId = targetEntityId;
                Anchor = anchor;
                Color = color;
                StartedAt = startedAt;
                NameplateScale = 1f;
            }

            public TextMeshProUGUI Text { get; }

            public ulong TargetEntityId { get; }

            public Vector3 Anchor { get; set; }

            // The size of the target's nameplate, which the number rises from.
            public float NameplateScale { get; set; }

            public Color Color { get; }

            public float StartedAt { get; }
        }
    }
}
