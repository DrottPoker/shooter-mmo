using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mmorpg.Client.Core;
using Mmorpg.Client.Ui;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace Mmorpg.Client.Editor.Ui
{
    // Builds the UI fonts and the UI theme from their sources. Fonts: static TextMesh Pro
    // assets of the title and body fonts, with LiberationSans UI SDF as their fallback. Theme:
    // every Sprite field of UiTheme takes the sprite of the same name below the UI sprite
    // folder, Silhouette_<slot id> files fill the equipment silhouettes and Tag_<tag id> files
    // the Bag slot silhouettes. The builder fills references and never changes a color or
    // measure, so values tuned in the Inspector stay.
    public static class UiThemeBuilder
    {
        public const string ThemePath = ClientAddresses.ContentRoot + ClientAddresses.UiTheme;
        public const string TitleFontPath = "Assets/Art/Fonts/Cinzel/Cinzel SDF.asset";
        public const string BodyFontPath = "Assets/Art/Fonts/AlegreyaSans/AlegreyaSans SDF.asset";
        public const string BodyBoldFontPath = "Assets/Art/Fonts/AlegreyaSans/AlegreyaSans Bold SDF.asset";
        public const string FallbackFontPath = "Assets/Art/Fonts/LiberationSans UI SDF.asset";

        public const string EquipmentSilhouettePrefix = "Silhouette_";
        public const string TagSilhouettePrefix = "Tag_";

        // Printable ASCII, Latin-1, and the punctuation that UI text and the Editor's overlays use.
        public const string FontCharacters = "20-7E,A0-FF,2013-2014,2018-201A,201C-201E,2022,2026,2030";

        private const float MinimumSpaceEm = 0.25f;
        private const int SamplingPointSize = 48;
        private const int AtlasPadding = 10;
        private const int AtlasSize = 1024;

        public const string BuildFontsMenuPath = "MMORPG/Tools/UI/Build UI Fonts";
        public const string BuildThemeMenuPath = "MMORPG/Tools/UI/Build UI Theme";

        [MenuItem(BuildFontsMenuPath)]
        public static void BuildFontsMenu()
        {
            Debug.Log(BuildFonts());
        }

        [MenuItem(BuildThemeMenuPath)]
        public static void BuildThemeMenu()
        {
            Debug.Log(BuildTheme());
        }

        // Makes the static font assets again from the font files and adds the punctuation to the
        // UI fallback font, so no common character reaches the dynamic fallback at run time.
        public static string BuildFonts()
        {
            var report = new List<string>();
            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackFontPath);
            report.Add(AddCharacters(fallback, FallbackFontPath));
            var bold = CreateStaticFont("Assets/Art/Fonts/AlegreyaSans/AlegreyaSans-Bold.ttf", BodyBoldFontPath, fallback, report);
            var body = CreateStaticFont("Assets/Art/Fonts/AlegreyaSans/AlegreyaSans-Regular.ttf", BodyFontPath, fallback, report);
            CreateStaticFont("Assets/Art/Fonts/Cinzel/Cinzel.ttf", TitleFontPath, fallback, report);
            if (body != null && bold != null)
            {
                // Bold text uses the real bold face instead of a thickened regular one.
                body.fontWeightTable[7].regularTypeface = bold;
                EditorUtility.SetDirty(body);
            }

            AssetDatabase.SaveAssets();
            return string.Join("\n", report);
        }

        public static string BuildTheme()
        {
            ReimportSprites();
            var theme = AssetDatabase.LoadAssetAtPath<UiTheme>(ThemePath);
            if (theme == null)
            {
                theme = ScriptableObject.CreateInstance<UiTheme>();
                AssetDatabase.CreateAsset(theme, ThemePath);
            }

            var sprites = LoadSprites();
            var missing = new List<string>();
            foreach (var field in typeof(UiTheme).GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => field.FieldType == typeof(Sprite)))
            {
                if (sprites.TryGetValue(field.Name, out var sprite))
                {
                    field.SetValue(theme, sprite);
                }
                else
                {
                    missing.Add(field.Name);
                }
            }

            theme.EquipmentSilhouettes = Entries(sprites, EquipmentSilhouettePrefix);
            theme.TagSilhouettes = Entries(sprites, TagSilhouettePrefix);
            theme.TitleFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TitleFontPath);
            theme.BodyFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BodyFontPath);
            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            return "UI theme built with " + sprites.Count + " sprites"
                + (missing.Count > 0 ? "; without a sprite: " + string.Join(", ", missing) : ".");
        }

        // The sprites of the UI sprite folder by file name.
        public static Dictionary<string, Sprite> LoadSprites()
        {
            var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { UiSpriteImportRules.SpriteFolder.TrimEnd('/') }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
                if (sprite != null)
                {
                    sprites[Path.GetFileNameWithoutExtension(path)] = sprite;
                }
            }

            return sprites;
        }

        private static void ReimportSprites()
        {
            foreach (var path in Directory.GetFiles(UiSpriteImportRules.SpriteFolder, "*.svg"))
            {
                AssetDatabase.ImportAsset(path.Replace('\\', '/'), ImportAssetOptions.ForceUpdate);
            }
        }

        private static List<UiTheme.SilhouetteEntry> Entries(Dictionary<string, Sprite> sprites, string prefix)
        {
            return sprites
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new UiTheme.SilhouetteEntry { Id = pair.Key.Substring(prefix.Length), Sprite = pair.Value })
                .ToList();
        }

        private static TMP_FontAsset CreateStaticFont(string fontPath, string assetPath, TMP_FontAsset fallback, List<string> report)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(fontPath);
            if (font == null)
            {
                report.Add("Missing font file " + fontPath);
                return null;
            }

            var asset = TMP_FontAsset.CreateFontAsset(
                font,
                SamplingPointSize,
                AtlasPadding,
                GlyphRenderMode.SDFAA,
                AtlasSize,
                AtlasSize,
                AtlasPopulationMode.Dynamic,
                false);
            asset.name = Path.GetFileNameWithoutExtension(assetPath);
            asset.TryAddCharacters(Characters(), out var missing);
            WidenSpace(asset);
            asset.atlasPopulationMode = AtlasPopulationMode.Static;
            if (fallback != null)
            {
                asset.fallbackFontAssetTable = new List<TMP_FontAsset> { fallback };
            }

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath) != null)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.CreateAsset(asset, assetPath);
            foreach (var texture in asset.atlasTextures)
            {
                texture.name = asset.name + " Atlas";
                AssetDatabase.AddObjectToAsset(texture, asset);
            }

            asset.material.name = asset.name + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            EditorUtility.SetDirty(asset);
            report.Add(assetPath + ": " + asset.characterTable.Count + " characters"
                + (string.IsNullOrEmpty(missing) ? string.Empty : ", missing " + Codes(missing)));
            return asset;
        }

        // Some faces, such as Alegreya Sans, draw a space of a sixth of an em, which closes up
        // words at small UI sizes; the space advances at least a quarter em.
        private static void WidenSpace(TMP_FontAsset asset)
        {
            if (!asset.characterLookupTable.TryGetValue(' ', out var space))
            {
                return;
            }

            var metrics = space.glyph.metrics;
            var advance = Mathf.Max(metrics.horizontalAdvance, MinimumSpaceEm * asset.faceInfo.pointSize);
            space.glyph.metrics = new GlyphMetrics(
                metrics.width,
                metrics.height,
                metrics.horizontalBearingX,
                metrics.horizontalBearingY,
                advance);
        }

        // Adds the characters a static font lacks, through a moment of dynamic population.
        private static string AddCharacters(TMP_FontAsset asset, string path)
        {
            if (asset == null)
            {
                return "Missing font asset " + path;
            }

            var lacking = new string(Characters().Where(character => !asset.HasCharacter(character)).ToArray());
            if (lacking.Length == 0)
            {
                return path + ": has every character";
            }

            var mode = asset.atlasPopulationMode;
            var before = asset.characterTable.Count;
            asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            asset.TryAddCharacters(lacking, out var missing);
            asset.atlasPopulationMode = mode;
            EditorUtility.SetDirty(asset);
            foreach (var texture in asset.atlasTextures)
            {
                EditorUtility.SetDirty(texture);
            }

            return path + ": " + before + " -> " + asset.characterTable.Count + " characters"
                + (string.IsNullOrEmpty(missing) ? string.Empty : ", missing " + Codes(missing));
        }

        private static string Characters()
        {
            var characters = new System.Text.StringBuilder();
            foreach (var range in FontCharacters.Split(','))
            {
                var bounds = range.Split('-');
                var first = Convert.ToInt32(bounds[0], 16);
                var last = Convert.ToInt32(bounds[bounds.Length - 1], 16);
                for (var code = first; code <= last; code++)
                {
                    characters.Append((char)code);
                }
            }

            return characters.ToString();
        }

        private static string Codes(string characters)
        {
            return string.Join(" ", characters.Select(character => ((int)character).ToString("X4")));
        }
    }
}
