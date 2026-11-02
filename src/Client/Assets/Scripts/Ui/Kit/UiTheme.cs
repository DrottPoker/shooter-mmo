using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using TMPro;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Ui
{
    // The look of the game's windows: fonts, text sizes, colors, measures, and sprites. Every
    // window and widget of the UI kit draws from one theme, so another design replaces sprites
    // and values here without code. Layout, what goes where, stays in the windows' code.
    [CreateAssetMenu(fileName = "UiTheme", menuName = "Mmorpg/UI Theme")]
    public sealed class UiTheme : ScriptableObject
    {
        [Header("Fonts")]
        [Tooltip("Window titles and section headings.")]
        public TMP_FontAsset TitleFont;
        [Tooltip("Labels, numbers, buttons, and tooltips.")]
        public TMP_FontAsset BodyFont;

        [Header("Text Sizes")]
        public float TitleSize = 24f;
        public float SectionSize = 15f;
        public float BodySize = 16f;
        public float LabelSize = 15f;
        public float SmallSize = 13f;
        public float QuantitySize = 15f;
        [Tooltip("Extra spacing between title letters, in em hundredths.")]
        public float TitleCharacterSpacing = 8f;

        [Header("Text Colors")]
        public Color TitleColor = new Color(0.93f, 0.85f, 0.66f, 1f);
        public Color TextColor = new Color(0.90f, 0.91f, 0.90f, 1f);
        public Color MutedTextColor = new Color(0.58f, 0.63f, 0.64f, 1f);
        public Color AccentColor = new Color(0.27f, 0.85f, 0.86f, 1f);
        public Color WarningColor = new Color(0.98f, 0.76f, 0.30f, 1f);
        public Color DangerColor = new Color(0.95f, 0.38f, 0.32f, 1f);
        public Color QuantityColor = new Color(0.98f, 0.96f, 0.90f, 1f);
        public Color GoldTextColor = new Color(0.96f, 0.80f, 0.40f, 1f);

        [Header("Element Colors")]
        [Tooltip("Tint of the window, slot, and button sprites, white for their own colors.")]
        public Color WindowTint = Color.white;
        public Color SlotTint = Color.white;
        public Color SeparatorColor = new Color(0.62f, 0.52f, 0.33f, 0.55f);
        public Color SilhouetteColor = new Color(0.62f, 0.66f, 0.66f, 0.30f);
        public Color HoverColor = new Color(0.95f, 0.85f, 0.60f, 0.85f);
        public Color DropValidColor = new Color(0.40f, 0.85f, 0.45f, 1f);
        public Color DropInvalidColor = new Color(0.95f, 0.35f, 0.30f, 1f);
        public Color RollbackColor = new Color(0.95f, 0.30f, 0.25f, 1f);
        public Color BlockedColor = new Color(0.80f, 0.32f, 0.28f, 0.85f);
        public Color MeterNormalColor = new Color(0.27f, 0.85f, 0.86f, 1f);
        public Color MeterSlowedColor = new Color(0.98f, 0.76f, 0.30f, 1f);
        public Color MeterHardCapColor = new Color(0.95f, 0.38f, 0.32f, 1f);
        [Range(0f, 1f)]
        [Tooltip("How visible a slot stays that the search does not match.")]
        public float SearchDimAlpha = 0.25f;

        [Header("Rarity Frame Colors")]
        [Tooltip("The frame of every item until items have a rarity.")]
        public Color RarityCommon = Color.white;

        [Header("Measures")]
        public float SlotSize = 54f;
        public float SlotSpacing = 6f;
        [Tooltip("Inset of an item icon inside its slot.")]
        public float IconInset = 6f;
        public float WindowPadding = 16f;
        public float HeaderHeight = 52f;
        public float SectionSpacing = 10f;
        [Tooltip("Distance of the windows from the screen edges.")]
        public float ScreenMargin = 24f;
        [Tooltip("Distance of every window's top edge from the top of the screen, below the HUD.")]
        public float WindowTop = 210f;
        public float WindowGap = 12f;
        public float ButtonHeight = 34f;
        public float FieldHeight = 34f;
        public float MeterHeight = 12f;
        public float EquipmentLabelWidth = 78f;
        public float EquipmentCenterWidth = 150f;

        [Header("Window Sprites")]
        [Tooltip("Nine-sliced window background with its frame.")]
        public Sprite WindowFrame;
        [Tooltip("Thin line under a window header or beside a section heading.")]
        public Sprite Separator;
        public Sprite CloseIcon;
        public Sprite EquipmentIcon;
        public Sprite InventoryIcon;
        public Sprite StorageIcon;
        public Sprite TooltipFrame;
        public Sprite DialogFrame;

        [Header("Control Sprites")]
        public Sprite ButtonFrame;
        public Sprite FieldFrame;
        public Sprite SearchIcon;
        public Sprite MeterTrack;
        public Sprite MeterFill;
        public Sprite MeterMarker;
        public Sprite CoinIcon;
        public Sprite BagIcon;
        public Sprite Pedestal;

        [Header("Slot Sprites")]
        public Sprite SlotBackground;
        [Tooltip("Nine-sliced frame around an item, tinted with its rarity color.")]
        public Sprite ItemFrame;
        [Tooltip("Nine-sliced highlight for hover and drop feedback, tinted.")]
        public Sprite SlotHighlight;
        public Sprite LockIcon;
        public Sprite HoodToggleIcon;

        [Header("Slot Silhouettes")]
        [Tooltip("The faint picture of an empty equipment slot, by equipment slot id.")]
        public List<SilhouetteEntry> EquipmentSilhouettes = new List<SilhouetteEntry>();
        [Tooltip("The faint picture of an empty specialized Bag slot, by accepted tag id.")]
        public List<SilhouetteEntry> TagSilhouettes = new List<SilhouetteEntry>();
        [Tooltip("The picture of an empty slot without its own silhouette.")]
        public Sprite FallbackSilhouette;

        // The theme the client's windows draw with. The handle keeps it, its sprites, and its
        // fonts loaded; the owner releases it with ClientAssets.Release. Without the bundled
        // theme the windows draw with defaults and plain shapes.
        public static UiTheme Load(out AsyncOperationHandle<UiTheme> handle)
        {
            if (ClientAssets.TryLoadNow(ClientAddresses.UiTheme, out handle, out var error))
            {
                return handle.Result;
            }

            Debug.LogWarning("The UI theme is unavailable: " + error + " Using the default look.");
            var fallback = CreateInstance<UiTheme>();
            fallback.TitleFont = TMP_Settings.defaultFontAsset;
            fallback.BodyFont = TMP_Settings.defaultFontAsset;
            return fallback;
        }

        // The silhouette of an empty equipment slot, or the fallback.
        public Sprite EquipmentSilhouette(string equipmentSlotId)
        {
            return Find(EquipmentSilhouettes, equipmentSlotId) ?? FallbackSilhouette;
        }

        // The silhouette of the first accepted tag that has one, or null for a general slot.
        public Sprite TagSilhouette(IReadOnlyList<string> acceptedTags)
        {
            if (acceptedTags == null || acceptedTags.Count == 0)
            {
                return null;
            }

            foreach (var tag in acceptedTags)
            {
                var sprite = Find(TagSilhouettes, tag);
                if (sprite != null)
                {
                    return sprite;
                }
            }

            return FallbackSilhouette;
        }

        private static Sprite Find(List<SilhouetteEntry> entries, string id)
        {
            if (entries == null || string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (var entry in entries)
            {
                if (entry != null && string.Equals(entry.Id, id, StringComparison.Ordinal))
                {
                    return entry.Sprite;
                }
            }

            return null;
        }

        [Serializable]
        public sealed class SilhouetteEntry
        {
            public string Id = string.Empty;
            public Sprite Sprite;
        }
    }
}
