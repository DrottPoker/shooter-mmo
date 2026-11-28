using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // Text over the world in the theme's fonts: a dark outline on the text's own material
    // instance, so it reads on bright ground. The world labels' outlined material belongs to the
    // default font and does not fit the theme's.
    internal static class HudText
    {
        public const float OutlineWidth = 0.2f;
        public static readonly Color OutlineColor = new Color(0f, 0f, 0f, 0.85f);

        public static void Outline(TextMeshProUGUI text)
        {
            // A theme without its fonts draws with the default font, as the windows do.
            if (text.font == null)
            {
                text.font = TMP_Settings.defaultFontAsset;
            }

            // fontMaterial makes the instance; the mobile SDF shader draws an outline only with
            // its keyword. The shader centres an outline on the glyph's edge, which eats half of
            // it out of a thin stroke, so the face widens by as much and keeps its weight.
            var material = text.fontMaterial;
            material.EnableKeyword(ShaderUtilities.Keyword_Outline);
            text.outlineWidth = OutlineWidth;
            text.outlineColor = OutlineColor;
            material.SetFloat(ShaderUtilities.ID_FaceDilate, OutlineWidth);
            text.UpdateMeshPadding();
        }
    }
}
