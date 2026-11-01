using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // Text drawn over the world, such as nameplates and damage numbers, in TextMesh Pro, so it
    // stays sharp at every size and screen scale. Every label uses TextMesh Pro's default font,
    // LiberationSans UI SDF, with one shared black-outlined material, so labels read over any
    // background and batch together. That font has a wide distance field for its size, so a
    // small label still gets an outline of about a pixel.
    public static class WorldLabels
    {
        // The outline and the face dilation that keeps the letters about as thick as without it.
        // Together they stay within the font's distance field, or every letter shows a dark box.
        public const float OutlineWidth = 0.3f;
        public const float FaceDilate = 0.15f;

        private static Material outlinedMaterial;

        // The default font of TextMesh Pro's settings.
        public static TMP_FontAsset Font
        {
            get { return TMP_Settings.defaultFontAsset; }
        }

        // The font's material with the outline turned on. TextMesh Pro's own outline preset
        // ships in every build with the same shader and keyword, so the variant exists there.
        // It follows the font's atlas, which the Editor replaces when it imports the font again.
        public static Material OutlinedMaterial
        {
            get
            {
                var font = Font;
                if (font != null
                    && (outlinedMaterial == null
                        || outlinedMaterial.GetTexture(ShaderUtilities.ID_MainTex)
                            != font.material.GetTexture(ShaderUtilities.ID_MainTex)))
                {
                    outlinedMaterial = new Material(font.material)
                    {
                        name = font.name + " World Label",
                        hideFlags = HideFlags.DontSave
                    };
                    outlinedMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
                    outlinedMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    outlinedMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, OutlineWidth);
                    outlinedMaterial.SetFloat(ShaderUtilities.ID_FaceDilate, FaceDilate);
                    ShaderUtilities.UpdateShaderRatios(outlinedMaterial);
                }

                return outlinedMaterial;
            }
        }

        // Entering Play Mode without a domain reload keeps static state, so the material of the
        // previous session goes before the first scene loads.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            if (outlinedMaterial != null)
            {
                Object.DestroyImmediate(outlinedMaterial);
            }

            outlinedMaterial = null;
        }

        // The font size whose capital letters and digits are the given height.
        public static float FontSizeForCapHeight(float capHeight)
        {
            var face = Font != null ? Font.faceInfo : default(UnityEngine.TextCore.FaceInfo);
            return face.capLine > 0f && face.pointSize > 0f
                ? capHeight * face.pointSize / face.capLine
                : capHeight;
        }

        // A label that never wraps, never takes a raycast, and shows plain text.
        public static TextMeshProUGUI Create(
            Transform parent,
            string name,
            float fontSize,
            HorizontalAlignmentOptions horizontal,
            VerticalAlignmentOptions vertical,
            Color color)
        {
            var label = TemporaryUiKit.CreateUiObject(name, parent).AddComponent<TextMeshProUGUI>();
            label.font = Font;
            label.fontSharedMaterial = OutlinedMaterial;
            label.fontSize = fontSize;
            label.fontStyle = FontStyles.Bold;
            label.horizontalAlignment = horizontal;
            label.verticalAlignment = vertical;
            label.color = color;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.richText = false;
            label.raycastTarget = false;
            label.text = string.Empty;
            return label;
        }
    }
}
