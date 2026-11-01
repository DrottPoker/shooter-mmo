using System.Linq;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // World labels are TextMesh Pro text with TextMesh Pro's default font and one outlined
    // material. These tests pin the font, the material, and the digit height the nameplates use.
    public sealed class WorldLabelTests
    {
        private const string TextMeshProResources = "Assets/TextMesh Pro/Resources";
        private const string UiFontPath = "Assets/Art/Fonts/LiberationSans UI SDF.asset";

        // The project's UI font is TextMesh Pro's default: LiberationSans at 48 points with a
        // padding of 10, a static atlas of the printable ASCII and Latin-1 characters, and
        // TextMesh Pro's dynamic fallback for the rest.
        [Test]
        public void TheDefaultFontIsTheStaticUiFontWithAWideDistanceField()
        {
            Assert.That(TMP_Settings.instance, Is.Not.Null);
            var font = WorldLabels.Font;
            Assert.That(font, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(font), Is.EqualTo(UiFontPath));
            Assert.That(font.faceInfo.familyName, Is.EqualTo("Liberation Sans"));
            Assert.That(font.faceInfo.pointSize, Is.EqualTo(48));
            Assert.That(font.atlasPadding, Is.EqualTo(10));

            // A static atlas never changes while the game runs, so playing leaves the asset as it is.
            Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static));
            var characters = new string(Enumerable.Range(32, 95)
                .Concat(Enumerable.Range(160, 96))
                .Select(code => (char)code)
                .ToArray());
            Assert.That(
                font.HasCharacters(characters, out var missing),
                Is.True,
                string.Join(",", missing ?? new System.Collections.Generic.List<char>()));
            Assert.That(font.fallbackFontAssetTable, Has.Count.EqualTo(1));
            Assert.That(font.fallbackFontAssetTable[0].atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Dynamic));
        }

        [Test]
        public void TheOutlinedMaterialDrawsTheFontAtlasWithABlackOutline()
        {
            var font = WorldLabels.Font;
            var material = WorldLabels.OutlinedMaterial;

            Assert.That(material, Is.Not.Null);
            Assert.That(material, Is.SameAs(WorldLabels.OutlinedMaterial));
            Assert.That(material.shader, Is.SameAs(font.material.shader));
            Assert.That(material.GetTexture(ShaderUtilities.ID_MainTex), Is.SameAs(font.atlasTexture));
            Assert.That(material.IsKeywordEnabled(ShaderUtilities.Keyword_Outline), Is.True);
            Assert.That(material.GetColor(ShaderUtilities.ID_OutlineColor), Is.EqualTo(Color.black));
            Assert.That(material.GetFloat(ShaderUtilities.ID_OutlineWidth), Is.EqualTo(WorldLabels.OutlineWidth));
            Assert.That(material.GetFloat(ShaderUtilities.ID_FaceDilate), Is.EqualTo(WorldLabels.FaceDilate));
            Assert.That(WorldLabels.OutlineWidth + WorldLabels.FaceDilate, Is.LessThanOrEqualTo(0.5f));
            Assert.That(AssetDatabase.Contains(material), Is.False);
        }

        // The outline is a shader feature. TextMesh Pro's own outline preset in its Resources
        // folder ships in every build and keeps that shader variant in the build.
        [Test]
        public void TheOutlineShaderVariantShipsInEveryBuild()
        {
            var shader = WorldLabels.Font.material.shader;
            var presets = AssetDatabase.FindAssets("t:Material", new[] { TextMeshProResources })
                .Select(guid => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(material => material != null
                    && material.shader == shader
                    && material.IsKeywordEnabled(ShaderUtilities.Keyword_Outline));

            Assert.That(presets, Is.Not.Empty);
        }

        [Test]
        public void ALabelSizedForACapHeightHasDigitsThatTall()
        {
            var parent = new GameObject("WorldLabelTest", typeof(RectTransform), typeof(Canvas));
            try
            {
                var label = WorldLabels.Create(
                    parent.transform,
                    "Level",
                    WorldLabels.FontSizeForCapHeight(WorldNameplates.BarHeight),
                    HorizontalAlignmentOptions.Left,
                    VerticalAlignmentOptions.Geometry,
                    Color.white);
                label.rectTransform.sizeDelta = new Vector2(48f, WorldNameplates.BarHeight);
                label.text = "5";
                label.ForceMeshUpdate();

                // The glyph's own height, without the padding its quad has for the outline.
                var digit = label.textInfo.characterInfo[0];
                var height = digit.textElement.glyph.metrics.height * digit.scale;
                Assert.That(height, Is.EqualTo(WorldNameplates.BarHeight).Within(0.5f));

                // Geometry alignment centers the digit's quad, padded evenly, on the box, so the
                // digit lines up with the bar.
                var center = (digit.topRight.y + digit.bottomLeft.y) * 0.5f;
                Assert.That(center, Is.EqualTo(label.rectTransform.rect.center.y).Within(1f));
                Assert.That(label.raycastTarget, Is.False);
                Assert.That(label.richText, Is.False);
                Assert.That(label.fontSharedMaterial, Is.SameAs(WorldLabels.OutlinedMaterial));
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }
    }
}
