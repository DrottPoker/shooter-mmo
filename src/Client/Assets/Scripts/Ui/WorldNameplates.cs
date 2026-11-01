using System.Collections.Generic;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The nameplates over the Mobs near the local character: the Mob's name over a health bar,
    // red for an aggressive Mob and yellow for a defensive one, with its health as numbers in
    // the bar and its level, as tall as the bar, to the right of it. The bar and the numbers
    // slide to every new health value, and a farther nameplate is smaller. It reads the combat
    // state and the actor views and decides nothing. Its texts are TextMesh Pro world labels.
    // Nameplates have a canvas of their own below the combat HUD, so damage numbers and frames
    // draw over them, and nothing on it takes a raycast. Nearer nameplates draw over farther
    // ones, and they are placed after the camera moved, so they stay on the heads they belong to.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(PresentationExecutionOrder.ScreenAnchoredInterface)]
    public sealed class WorldNameplates : MonoBehaviour
    {
        public const int CanvasSortingOrder = TemporaryCombatHud.CanvasSortingOrder - 10;

        // A nameplate's bottom edge sits this far above the top of the Mob, and at full size the
        // nameplate is this high on the 1080 pixel reference screen. Damage numbers rise from
        // above it.
        public const float AnchorLift = 0.35f;
        public const float Height = BarHeight + NameGap + NameHeight;

        // The bar, and the height of the digits of the health in it and of the level beside it.
        public const float BarHeight = 13f;
        public const float HealthCapHeight = 7.5f;

        private const float BarWidth = 120f;
        private const float NameHeight = 18f;
        private const float NameFontSize = 14f;
        private const float NameGap = 1f;
        private const float LevelGap = 4f;
        private const float LevelWidth = 48f;
        private static readonly Color TrackColor = new Color(0f, 0f, 0f, 0.72f);
        private static readonly Color TrackBorderColor = new Color(0f, 0f, 0f, 0.9f);
        private static readonly NearestLast DrawOrder = new NearestLast();

        private readonly List<ulong> combatantIds = new List<ulong>();
        private readonly Dictionary<ulong, Nameplate> nameplates = new Dictionary<ulong, Nameplate>();
        private readonly Stack<Nameplate> idle = new Stack<Nameplate>();
        private readonly List<Nameplate> shown = new List<Nameplate>();
        private readonly List<ulong> retired = new List<ulong>();
        private CombatClientController combatController;
        private CombatantViewLocator views;
        private RectTransform layer;
        private bool subscribed;

        // How many nameplates show right now.
        public int VisibleCount
        {
            get { return shown.Count; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(CombatClientController combat, CombatantViewLocator combatantViews)
        {
            combatController = combat;
            views = combatantViews;
        }

        // The nameplate of a Mob, while it shows.
        public bool TryGetNameplate(ulong entityId, out NameplateView view)
        {
            view = default(NameplateView);
            if (!nameplates.TryGetValue(entityId, out var nameplate) || !nameplate.Root.gameObject.activeSelf)
            {
                return false;
            }

            view = new NameplateView(
                nameplate.Name.text,
                nameplate.Health.text,
                nameplate.Level.text,
                nameplate.Fill.color,
                nameplate.Fill.rectTransform.anchorMax.x,
                nameplate.Root.anchoredPosition,
                nameplate.Root.localScale.x);
            return true;
        }

        private void Start()
        {
            BuildCanvas();
            if (combatController != null)
            {
                combatController.State.Changed += OnCombatStateChanged;
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
            }

            subscribed = false;
        }

        // The combatants in view change rarely, so the list is kept instead of read every frame.
        private void OnCombatStateChanged()
        {
            combatantIds.Clear();
            if (combatController == null)
            {
                return;
            }

            var combatants = combatController.State.Combatants;
            for (var index = 0; index < combatants.Count; index++)
            {
                combatantIds.Add(combatants[index].EntityId);
            }
        }

        private void LateUpdate()
        {
            if (layer == null)
            {
                return;
            }

            shown.Clear();
            var player = views != null ? views.LocalPlayer : null;
            var playerCamera = views != null ? views.PlayerCamera : null;
            if (combatController != null && player != null && playerCamera != null)
            {
                var now = Time.unscaledTime;
                var origin = player.transform.position;
                for (var index = 0; index < combatantIds.Count; index++)
                {
                    if (combatController.State.TryGetCombatant(combatantIds[index], out var state))
                    {
                        Place(state, origin, playerCamera, now);
                    }
                }
            }

            RetireUnshown();
            shown.Sort(DrawOrder);
            for (var index = 0; index < shown.Count; index++)
            {
                var root = shown[index].Root;
                if (root.GetSiblingIndex() != index)
                {
                    root.SetSiblingIndex(index);
                }
            }
        }

        private void Place(RealtimeCombatantState state, Vector3 origin, Camera playerCamera, float now)
        {
            if (!views.TryGetMob(state.EntityId, out var mob)
                || !views.TryGetView(state.EntityId, out var view))
            {
                return;
            }

            var sqrDistance = (view.Position - origin).sqrMagnitude;
            if (!NameplatePresentation.ShowsMobNameplate(sqrDistance, state.LifeState)
                || !TryProject(playerCamera, view.Top + (Vector3.up * AnchorLift), out var local))
            {
                return;
            }

            if (!nameplates.TryGetValue(state.EntityId, out var nameplate))
            {
                nameplate = idle.Count > 0 ? idle.Pop() : CreateNameplate();
                nameplate.Bind(view.DisplayName, mob.Level, mob.Aggression);
                nameplates.Add(state.EntityId, nameplate);
            }

            nameplate.Root.gameObject.SetActive(true);
            nameplate.Root.anchoredPosition = local;
            nameplate.Scale(NameplatePresentation.MobNameplateScale(Mathf.Sqrt(sqrDistance)));
            nameplate.Show(state.Health, state.MaximumHealth, now);
            nameplate.SqrDistance = sqrDistance;
            nameplate.ShownThisFrame = true;
            shown.Add(nameplate);
        }

        // A Mob that left the range, died, or despawned gives its nameplate back; when it shows
        // again, its health shows at once.
        private void RetireUnshown()
        {
            retired.Clear();
            foreach (var pair in nameplates)
            {
                if (pair.Value.ShownThisFrame)
                {
                    pair.Value.ShownThisFrame = false;
                    continue;
                }

                retired.Add(pair.Key);
            }

            for (var index = 0; index < retired.Count; index++)
            {
                var nameplate = nameplates[retired[index]];
                nameplates.Remove(retired[index]);
                nameplate.Root.gameObject.SetActive(false);
                idle.Push(nameplate);
            }
        }

        private bool TryProject(Camera playerCamera, Vector3 worldPosition, out Vector2 local)
        {
            local = Vector2.zero;
            var screen = playerCamera.WorldToScreenPoint(worldPosition);
            return screen.z > 0f
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(layer, screen, null, out local);
        }

        private void BuildCanvas()
        {
            var canvasObject = new GameObject(
                "WorldNameplateCanvas",
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

            layer = TemporaryUiKit.CreateUiObject("NameplateLayer", canvasObject.transform)
                .GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(layer, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        // The root's pivot is the anchor point over the head; the bar is centered on it, the name
        // sits above the bar, and the level hangs off its right end.
        private Nameplate CreateNameplate()
        {
            var root = TemporaryUiKit.CreateUiObject("MobNameplate", layer).GetComponent<RectTransform>();
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0f);
            root.sizeDelta = new Vector2(BarWidth, Height);

            var track = TemporaryUiKit.CreateUiObject("HealthBar", root).GetComponent<RectTransform>();
            Layout(track, new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(BarWidth, BarHeight));
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = TrackColor;
            trackImage.raycastTarget = false;
            TemporaryUiKit.AddOutline(trackImage, TrackBorderColor);

            var fill = TemporaryUiKit.CreateUiObject("Fill", track).AddComponent<Image>();
            fill.raycastTarget = false;
            TemporaryUiKit.Stretch(fill.rectTransform, Vector2.zero, Vector2.one, new Vector2(1f, 1f), new Vector2(-1f, -1f));

            // Geometry alignment centers the digits themselves on the bar.
            var health = WorldLabels.Create(
                track,
                "Health",
                WorldLabels.FontSizeForCapHeight(HealthCapHeight),
                HorizontalAlignmentOptions.Center,
                VerticalAlignmentOptions.Geometry,
                TemporaryUiKit.TextPrimary);
            TemporaryUiKit.Stretch(health.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var name = WorldLabels.Create(
                root,
                "Name",
                NameFontSize,
                HorizontalAlignmentOptions.Center,
                VerticalAlignmentOptions.Bottom,
                TemporaryUiKit.TextPrimary);
            Layout(name.rectTransform, new Vector2(0.5f, 0f), new Vector2(0f, BarHeight + NameGap), new Vector2(BarWidth * 2f, NameHeight));

            var level = WorldLabels.Create(
                root,
                "Level",
                WorldLabels.FontSizeForCapHeight(BarHeight),
                HorizontalAlignmentOptions.Left,
                VerticalAlignmentOptions.Geometry,
                TemporaryUiKit.TextPrimary);
            Layout(
                level.rectTransform,
                new Vector2(0f, 0f),
                new Vector2((BarWidth * 0.5f) + LevelGap, 0f),
                new Vector2(LevelWidth, BarHeight));

            root.gameObject.SetActive(false);
            return new Nameplate(root, fill, health, name, level);
        }

        private static void Layout(RectTransform rect, Vector2 pivot, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        // One nameplate and the slide of the health it shows. Texts change only when their
        // values do, so a frame allocates no strings.
        private sealed class Nameplate
        {
            private readonly NameplateHealthSlide slide =
                new NameplateHealthSlide(NameplatePresentation.HealthSlideSeconds);
            private int shownHealth = -1;
            private int shownMaximum = -1;
            private float shownFraction = -1f;
            private float shownScale = -1f;

            public Nameplate(
                RectTransform root,
                Image fill,
                TextMeshProUGUI health,
                TextMeshProUGUI name,
                TextMeshProUGUI level)
            {
                Root = root;
                Fill = fill;
                Health = health;
                Name = name;
                Level = level;
            }

            public RectTransform Root { get; }

            public Image Fill { get; }

            public TextMeshProUGUI Health { get; }

            public TextMeshProUGUI Name { get; }

            public TextMeshProUGUI Level { get; }

            public float SqrDistance { get; set; }

            public bool ShownThisFrame { get; set; }

            // A pooled nameplate starts over for another Mob.
            public void Bind(string displayName, int level, RealtimeWorldActorAggression aggression)
            {
                Name.text = displayName ?? string.Empty;
                Level.text = NameplatePresentation.LevelText(level);
                Fill.color = NameplatePresentation.MobHealthColor(aggression);
                slide.Reset();
                shownHealth = -1;
                shownMaximum = -1;
                shownFraction = -1f;
                shownScale = -1f;
            }

            // The scale changes only when it does, so a resting nameplate dirties nothing.
            public void Scale(float scale)
            {
                if (!Mathf.Approximately(scale, shownScale))
                {
                    shownScale = scale;
                    Root.localScale = new Vector3(scale, scale, 1f);
                }
            }

            public void Show(int health, int maximumHealth, float now)
            {
                slide.Set(health, maximumHealth, now);
                var displayed = slide.DisplayedHealth(now);
                if (displayed != shownHealth || slide.Maximum != shownMaximum)
                {
                    shownHealth = displayed;
                    shownMaximum = slide.Maximum;
                    Health.text = NameplatePresentation.HealthText(displayed, slide.Maximum);
                }

                var fraction = slide.Fraction(now);
                if (!Mathf.Approximately(fraction, shownFraction))
                {
                    shownFraction = fraction;
                    Fill.rectTransform.anchorMax = new Vector2(fraction, 1f);
                }
            }
        }

        // Farther nameplates first, so the nearest draws on top.
        private sealed class NearestLast : IComparer<Nameplate>
        {
            public int Compare(Nameplate first, Nameplate second)
            {
                return second.SqrDistance.CompareTo(first.SqrDistance);
            }
        }
    }

    // What a Mob's nameplate shows, for tests and diagnostics.
    public readonly struct NameplateView
    {
        public NameplateView(
            string name,
            string health,
            string level,
            Color barColor,
            float barFraction,
            Vector2 position,
            float scale)
        {
            Name = name;
            Health = health;
            Level = level;
            BarColor = barColor;
            BarFraction = barFraction;
            Position = position;
            Scale = scale;
        }

        public string Name { get; }

        public string Health { get; }

        public string Level { get; }

        public Color BarColor { get; }

        public float BarFraction { get; }

        public Vector2 Position { get; }

        public float Scale { get; }
    }
}
