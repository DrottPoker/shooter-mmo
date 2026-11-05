using System;
using System.Collections.Generic;
using Mmorpg.Client.Combat;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Quests;
using Mmorpg.Client.WorldActors;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The quest markers over the NPCs near the local character: a yellow ! for a quest to accept,
    // a grey ! for one that needs a higher level, a yellow ? for something to turn in, talk
    // about, or hand over, and a grey ? for an active quest that the NPC completes once it is
    // ready, as QuestMarkers derives them. It reads the quest state, the inventory, and the actor
    // views and decides nothing. The markers have a canvas of their own beside the nameplates',
    // take no raycasts, and are placed after the camera moved, so they stay over the heads. A
    // farther marker is smaller, like a nameplate.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(PresentationExecutionOrder.ScreenAnchoredInterface)]
    public sealed class WorldQuestMarkers : MonoBehaviour
    {
        public const int CanvasSortingOrder = WorldNameplates.CanvasSortingOrder;

        // Every NPC this near the local character shows its marker, as far as a Mob's nameplate.
        public const float RangeMetres = NameplatePresentation.MobRangeMetres;

        // The marker's bottom sits this far above the top of the NPC.
        public const float AnchorLift = 0.3f;

        // The height of the marker's glyph at full size on the 1080 pixel reference screen.
        public const float GlyphCapHeight = 32f;

        public static readonly Color UrgentColor = new Color(1f, 0.82f, 0.1f, 1f);
        public static readonly Color WaitingColor = new Color(0.62f, 0.62f, 0.62f, 1f);

        // The height above the root of an NPC without interaction bounds.
        private const float FallbackHeight = 1.8f;

        private readonly Dictionary<string, QuestMarker> markers =
            new Dictionary<string, QuestMarker>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, MarkerLabel> labels = new Dictionary<ulong, MarkerLabel>();
        private readonly Stack<MarkerLabel> idle = new Stack<MarkerLabel>();
        private readonly List<ulong> retired = new List<ulong>();
        private QuestClientController quests;
        private InventoryClientController inventory;
        private WorldActorPresentationController actors;
        private CombatantViewLocator views;
        private Func<string, int> carriedCount;
        private Func<string, int> ownedCount;
        private RectTransform layer;
        private bool subscribed;

        // How many markers show right now.
        public int VisibleCount
        {
            get { return labels.Count; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            QuestClientController questController,
            InventoryClientController inventoryController,
            WorldActorPresentationController actorPresentation,
            CombatantViewLocator combatantViews)
        {
            quests = questController;
            inventory = inventoryController;
            actors = actorPresentation;
            views = combatantViews;
            carriedCount = QuestCarriedItems.Counter(inventory?.State);
            ownedCount = QuestCarriedItems.OwnedQuestGrantCounter(inventory?.State);
        }

        // The marker over an NPC, while it shows.
        public bool TryGetMarker(ulong entityId, out QuestMarker marker, out string text, out Color color)
        {
            marker = QuestMarker.None;
            text = string.Empty;
            color = default(Color);
            if (!labels.TryGetValue(entityId, out var label))
            {
                return false;
            }

            marker = label.Marker;
            text = label.Text.text;
            color = label.Text.color;
            return true;
        }

        public static string TextOf(QuestMarker marker)
        {
            switch (marker)
            {
                case QuestMarker.HigherLevelQuest:
                case QuestMarker.Available:
                    return "!";
                case QuestMarker.InProgress:
                case QuestMarker.Ready:
                    return "?";
                default:
                    return string.Empty;
            }
        }

        public static Color ColorOf(QuestMarker marker)
        {
            return marker == QuestMarker.Available || marker == QuestMarker.Ready
                ? UrgentColor
                : WaitingColor;
        }

        // The size of a marker at a distance from the local character, as a nameplate's.
        public static float ScaleAt(float distance)
        {
            var nearness = Mathf.InverseLerp(RangeMetres, NameplatePresentation.FullSizeDistanceMetres, distance);
            return Mathf.Lerp(NameplatePresentation.FarScale, 1f, nearness);
        }

        private void Start()
        {
            BuildCanvas();
            if (quests != null)
            {
                quests.State.Changed += Forget;
            }

            if (inventory != null)
            {
                inventory.State.Changed += Forget;
            }

            subscribed = true;
            LateUpdate();
        }

        private void OnDestroy()
        {
            if (!subscribed)
            {
                return;
            }

            if (quests != null)
            {
                quests.State.Changed -= Forget;
            }

            if (inventory != null)
            {
                inventory.State.Changed -= Forget;
            }

            subscribed = false;
        }

        // A quest change, a new level, or an inventory change can change any marker, so each is
        // derived again when its NPC is next in view.
        private void Forget()
        {
            markers.Clear();
        }

        private void LateUpdate()
        {
            if (layer == null)
            {
                return;
            }

            var player = views != null ? views.LocalPlayer : null;
            var playerCamera = views != null ? views.PlayerCamera : null;
            if (actors != null && quests != null && quests.State.IsReady && player != null && playerCamera != null)
            {
                var origin = player.transform.position;
                foreach (var view in actors.Views)
                {
                    if (view is NpcView && view.gameObject.activeInHierarchy)
                    {
                        Place(view, origin, playerCamera);
                    }
                }
            }

            RetireUnshown();
        }

        private void Place(WorldActorView view, Vector3 origin, Camera playerCamera)
        {
            var marker = MarkerOf(view.ActorDefinitionId);
            var position = view.transform.position;
            var sqrDistance = (position - origin).sqrMagnitude;
            var height = view.TargetHeight > 0f ? view.TargetHeight : FallbackHeight;
            if (marker == QuestMarker.None
                || sqrDistance > RangeMetres * RangeMetres
                || !TryProject(playerCamera, position + (Vector3.up * (height + AnchorLift)), out var local))
            {
                return;
            }

            if (!labels.TryGetValue(view.TargetEntityId, out var label))
            {
                label = idle.Count > 0 ? idle.Pop() : CreateLabel();
                labels.Add(view.TargetEntityId, label);
            }

            label.Show(marker, local, ScaleAt(Mathf.Sqrt(sqrDistance)));
        }

        private QuestMarker MarkerOf(string actorDefinitionId)
        {
            if (string.IsNullOrEmpty(actorDefinitionId))
            {
                return QuestMarker.None;
            }

            if (!markers.TryGetValue(actorDefinitionId, out var marker))
            {
                marker = QuestMarkers.For(actorDefinitionId, quests.State, carriedCount, ownedCount);
                markers.Add(actorDefinitionId, marker);
            }

            return marker;
        }

        // An NPC that left the range, lost its marker, or despawned gives its label back.
        private void RetireUnshown()
        {
            retired.Clear();
            foreach (var pair in labels)
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
                var label = labels[retired[index]];
                labels.Remove(retired[index]);
                label.Hide();
                idle.Push(label);
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
                "WorldQuestMarkerCanvas",
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

            layer = TemporaryUiKit.CreateUiObject("QuestMarkerLayer", canvasObject.transform)
                .GetComponent<RectTransform>();
            TemporaryUiKit.Stretch(layer, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        // The label's pivot is the anchor point over the head, at the bottom of the glyph.
        private MarkerLabel CreateLabel()
        {
            var text = WorldLabels.Create(
                layer,
                "QuestMarker",
                WorldLabels.FontSizeForCapHeight(GlyphCapHeight),
                HorizontalAlignmentOptions.Center,
                VerticalAlignmentOptions.Bottom,
                UrgentColor);
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(GlyphCapHeight * 2f, GlyphCapHeight * 1.5f);
            text.gameObject.SetActive(false);
            return new MarkerLabel(text);
        }

        // One marker label. Its text and color change only when its marker does, so a frame
        // rebuilds no text mesh.
        private sealed class MarkerLabel
        {
            private float shownScale = -1f;

            public MarkerLabel(TextMeshProUGUI text)
            {
                Text = text;
            }

            public TextMeshProUGUI Text { get; }

            public QuestMarker Marker { get; private set; }

            public bool ShownThisFrame { get; set; }

            public void Show(QuestMarker marker, Vector2 position, float scale)
            {
                if (Marker != marker || !Text.gameObject.activeSelf)
                {
                    Marker = marker;
                    Text.text = TextOf(marker);
                    Text.color = ColorOf(marker);
                    Text.gameObject.SetActive(true);
                }

                Text.rectTransform.anchoredPosition = position;
                if (!Mathf.Approximately(shownScale, scale))
                {
                    shownScale = scale;
                    Text.rectTransform.localScale = new Vector3(scale, scale, 1f);
                }

                ShownThisFrame = true;
            }

            public void Hide()
            {
                Marker = QuestMarker.None;
                ShownThisFrame = false;
                Text.gameObject.SetActive(false);
            }
        }
    }
}
