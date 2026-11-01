using System;
using Mmorpg.Client.Core;
using Mmorpg.WorldData.Items.Presentation;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Every image below the item icon folder imports as one UI Sprite. Mipmaps keep an icon
    // smooth where a slot draws it smaller than its texture.
    public sealed class ItemIconImportRules : AssetPostprocessor
    {
        public const string IconPathPrefix =
            ClientAddresses.ContentRoot + ItemPresentationCatalogFormat.IconAddressPrefix;
        public const int MaximumIconSize = 256;

        public static bool AppliesTo(string assetPath)
        {
            return assetPath != null && assetPath.StartsWith(IconPathPrefix, StringComparison.Ordinal);
        }

        public static void ApplySettings(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.maxTextureSize = MaximumIconSize;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.crunchedCompression = false;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
        }

        private void OnPreprocessTexture()
        {
            if (AppliesTo(assetPath))
            {
                ApplySettings((TextureImporter)assetImporter);
            }
        }
    }
}
