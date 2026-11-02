using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor.Ui
{
    // Every SVG in the UI sprite folder imports as one textured UI Sprite: at the size and
    // nine-slice border its entry in ui-sprites.json names, or square at the default size
    // without a border. The SVG files are the source of the UI's look; a changed entry takes
    // effect when UiThemeBuilder reimports the sprites.
    public sealed class UiSpriteImportRules : AssetPostprocessor
    {
        public const string SpriteFolder = "Assets/Art/Ui/Sprites/";
        public const string ManifestPath = SpriteFolder + "ui-sprites.json";

        // Raise to reimport every UI sprite after a change of these rules.
        private const uint Version = 1;

        public static bool AppliesTo(string assetPath)
        {
            return assetPath != null
                && assetPath.StartsWith(SpriteFolder, StringComparison.Ordinal)
                && assetPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
        }

        public static UiSpriteManifest LoadManifest()
        {
            if (!File.Exists(ManifestPath))
            {
                return new UiSpriteManifest();
            }

            return JsonUtility.FromJson<UiSpriteManifest>(File.ReadAllText(ManifestPath)) ?? new UiSpriteManifest();
        }

        public static void Apply(AssetImporter importer, UiSpriteManifest.Entry entry)
        {
            var serialized = new SerializedObject(importer);
            SetEnum(serialized, "m_SvgType", "TexturedSprite");
            SetEnum(serialized, "m_TexturedSpriteMeshType", "FullRect");
            SetEnum(serialized, "m_WrapMode", "Clamp");
            SetEnum(serialized, "m_FilterMode", "Bilinear");
            // The whole view box becomes the texture, so an icon keeps its margins and shape.
            SetEnum(serialized, "m_ViewportOptions", "PreserveViewport");
            serialized.FindProperty("m_PreserveViewport").boolValue = true;
            serialized.FindProperty("m_KeepTextureAspectRatio").boolValue = false;
            serialized.FindProperty("m_TextureWidth").intValue = entry.width;
            serialized.FindProperty("m_TextureHeight").intValue = entry.height;
            serialized.FindProperty("m_TextureSize").intValue = Mathf.Max(entry.width, entry.height);
            serialized.FindProperty("m_SampleCount").intValue = 8;
            var border = entry.border != null && entry.border.Length == 4
                ? new Vector4(entry.border[0], entry.border[1], entry.border[2], entry.border[3])
                : Vector4.zero;
            serialized.FindProperty("m_SpriteData.SpriteBorder").vector4Value = border;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public override uint GetVersion()
        {
            return Version;
        }

        private void OnPreprocessAsset()
        {
            if (AppliesTo(assetPath))
            {
                Apply(assetImporter, LoadManifest().Find(Path.GetFileName(assetPath)));
            }
        }

        private static void SetEnum(SerializedObject serialized, string propertyPath, string value)
        {
            var property = serialized.FindProperty(propertyPath);
            var index = Array.IndexOf(property.enumNames, value);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    "The SVG importer has no " + propertyPath + " named " + value + ".");
            }

            property.enumValueIndex = index;
        }
    }

    // The import size and nine-slice border of the UI sprites, by file name.
    [Serializable]
    public sealed class UiSpriteManifest
    {
        public int defaultSize = 128;
        public Entry[] sprites = Array.Empty<Entry>();

        public Entry Find(string fileName)
        {
            foreach (var entry in sprites ?? Array.Empty<Entry>())
            {
                if (entry != null && string.Equals(entry.file, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return new Entry { file = fileName, width = defaultSize, height = defaultSize };
        }

        [Serializable]
        public sealed class Entry
        {
            public string file = string.Empty;
            public int width = 128;
            public int height = 128;

            // Left, bottom, right, top, in texture pixels; none draws the sprite whole.
            public int[] border = Array.Empty<int>();
        }
    }
}
