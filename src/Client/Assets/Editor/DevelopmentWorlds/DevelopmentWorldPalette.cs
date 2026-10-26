using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Editor
{
    // The shared development World materials. Every color has one meaning in both Worlds,
    // and the ground carries a meter grid so distances can be judged at a glance.
    internal sealed class DevelopmentWorldPalette
    {
        public const string MaterialDirectory = "Assets/Art/Materials/DevelopmentWorlds";
        public const string GridTexturePath = MaterialDirectory + "/T_DevelopmentWorld_GroundGrid.png";

        // One texture tile covers four meters: thin lines every meter, a strong line every four.
        public const float GridTileMeters = 4f;
        private const int GridTexturePixels = 256;

        private DevelopmentWorldPalette()
        {
        }

        public Material Ground { get; private set; }

        public Material Path { get; private set; }

        public Material Plaza { get; private set; }

        public Material Structure { get; private set; }

        public Material Roof { get; private set; }

        public Material Boundary { get; private set; }

        public Material Traversal { get; private set; }

        public Material Passable { get; private set; }

        public Material Blocked { get; private set; }

        public Material Rock { get; private set; }

        public Material Trunk { get; private set; }

        public Material Foliage { get; private set; }

        public Material Sandbox { get; private set; }

        public Material Marker { get; private set; }

        public Material SignPost { get; private set; }

        public Material SignBoard { get; private set; }

        // The ground material is per World, because its grid tiling follows the ground size.
        public static DevelopmentWorldPalette Create(string worldId, float groundSize)
        {
            EnsureFolder("Assets/Art/Materials", "DevelopmentWorlds");
            var grid = CreateOrUpdateGridTexture();
            var groundName = "M_" + ToAssetName(worldId) + "_Ground";
            return new DevelopmentWorldPalette
            {
                Ground = CreateOrUpdateMaterial(groundName, new Color(0.36f, 0.47f, 0.33f), grid, groundSize / GridTileMeters),
                Path = CreateOrUpdateMaterial("M_DevelopmentWorld_Path", new Color(0.55f, 0.47f, 0.35f)),
                Plaza = CreateOrUpdateMaterial("M_DevelopmentWorld_Plaza", new Color(0.52f, 0.52f, 0.50f)),
                Structure = CreateOrUpdateMaterial("M_DevelopmentWorld_Structure", new Color(0.42f, 0.50f, 0.58f)),
                Roof = CreateOrUpdateMaterial("M_DevelopmentWorld_Roof", new Color(0.50f, 0.30f, 0.25f)),
                Boundary = CreateOrUpdateMaterial("M_DevelopmentWorld_Boundary", new Color(0.16f, 0.18f, 0.20f)),
                Traversal = CreateOrUpdateMaterial("M_DevelopmentWorld_Traversal", new Color(0.80f, 0.52f, 0.20f)),
                Passable = CreateOrUpdateMaterial("M_DevelopmentWorld_Passable", new Color(0.30f, 0.66f, 0.34f)),
                Blocked = CreateOrUpdateMaterial("M_DevelopmentWorld_Blocked", new Color(0.74f, 0.24f, 0.22f)),
                Rock = CreateOrUpdateMaterial("M_DevelopmentWorld_Rock", new Color(0.45f, 0.42f, 0.38f)),
                Trunk = CreateOrUpdateMaterial("M_DevelopmentWorld_Trunk", new Color(0.33f, 0.24f, 0.16f)),
                Foliage = CreateOrUpdateMaterial("M_DevelopmentWorld_Foliage", new Color(0.18f, 0.34f, 0.18f)),
                Sandbox = CreateOrUpdateMaterial("M_DevelopmentWorld_Sandbox", new Color(0.28f, 0.52f, 0.52f)),
                Marker = CreateOrUpdateMaterial("M_DevelopmentWorld_Marker", new Color(0.26f, 0.72f, 0.82f)),
                SignPost = CreateOrUpdateMaterial("M_DevelopmentWorld_SignPost", new Color(0.30f, 0.22f, 0.14f)),
                SignBoard = CreateOrUpdateMaterial("M_DevelopmentWorld_SignBoard", new Color(0.12f, 0.13f, 0.15f))
            };
        }

        private static string ToAssetName(string worldId)
        {
            var parts = worldId.Split('-');
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = char.ToUpperInvariant(parts[index][0]) + parts[index].Substring(1);
            }

            return string.Concat(parts);
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + child))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        // White with grey lines, so the material color tints it. The texture is written as a
        // PNG once and imported with repeat wrapping, mipmaps, and anisotropic filtering.
        private static Texture2D CreateOrUpdateGridTexture()
        {
            var texture = new Texture2D(GridTexturePixels, GridTexturePixels, TextureFormat.RGBA32, false);
            var pixelsPerMeter = GridTexturePixels / (int)GridTileMeters;
            var pixels = new Color32[GridTexturePixels * GridTexturePixels];
            for (var y = 0; y < GridTexturePixels; y++)
            {
                for (var x = 0; x < GridTexturePixels; x++)
                {
                    var major = x < 2 || y < 2 || x >= GridTexturePixels - 2 || y >= GridTexturePixels - 2;
                    var minor = x % pixelsPerMeter < 1 || y % pixelsPerMeter < 1;
                    var value = major ? (byte)150 : minor ? (byte)205 : (byte)255;
                    pixels[(y * GridTexturePixels) + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);

            var fullPath = System.IO.Path.GetFullPath(GridTexturePath);
            if (!File.Exists(fullPath) || !BytesEqual(File.ReadAllBytes(fullPath), png))
            {
                File.WriteAllBytes(fullPath, png);
                AssetDatabase.ImportAsset(GridTexturePath, ImportAssetOptions.ForceSynchronousImport);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(GridTexturePath);
            if (importer.wrapMode != TextureWrapMode.Repeat
                || !importer.mipmapEnabled
                || importer.anisoLevel != 8
                || importer.filterMode != FilterMode.Trilinear
                || importer.alphaSource != TextureImporterAlphaSource.None)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.anisoLevel = 8;
                importer.filterMode = FilterMode.Trilinear;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexturePath);
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (var index = 0; index < first.Length; index++)
            {
                if (first[index] != second[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static Material CreateOrUpdateMaterial(
            string name,
            Color color,
            Texture2D baseMap = null,
            float tiling = 1f)
        {
            var path = MaterialDirectory + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shaderName = GraphicsSettings.currentRenderPipeline == null
                ? "Standard"
                : "Universal Render Pipeline/Lit";
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "The shader required by the active render pipeline is unavailable: " + shaderName + ".");
            }

            if (material == null)
            {
                material = new Material(shader)
                {
                    name = name
                };
                AssetDatabase.CreateAsset(material, path);
            }

            // The palette goes onto a copy in the form the shader's Inspector keeps, and the
            // asset changes only when that form differs from an untouched copy, so a rebuild
            // with the same palette leaves every material file as it is. Both copies carry
            // every property of the current shader, so a material saved before a shader added
            // properties, such as URP's screen space reflections, does not count as changed.
            var untouched = new Material(material)
            {
                name = material.name
            };
            var target = new Material(material)
            {
                name = material.name
            };
            try
            {
                if (target.shader != shader)
                {
                    target.shader = shader;
                }

                SetColor(target, "_BaseColor", color);
                SetColor(target, "_Color", color);
                foreach (var textureProperty in new[] { "_BaseMap", "_MainTex" })
                {
                    if (target.HasProperty(textureProperty))
                    {
                        target.SetTexture(textureProperty, baseMap);
                        target.SetTextureScale(textureProperty, new Vector2(tiling, tiling));
                    }
                }

                SetFloat(target, "_Smoothness", 0.12f);
                SetFloat(target, "_Glossiness", 0.12f);
                ValidateAsTheInspectorDoes(target);
                if (EditorJsonUtility.ToJson(target) != EditorJsonUtility.ToJson(untouched))
                {
                    EditorUtility.CopySerialized(target, material);
                    EditorUtility.SetDirty(material);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(untouched);
                UnityEngine.Object.DestroyImmediate(target);
            }

            return material;
        }

        // The shader's Inspector validates a material whenever it shows it, and URP Lit then
        // derives the legacy _Color from _BaseColor with slightly different floats. A material
        // saved without that step changed its file the first time anyone selected it.
        public static void ValidateAsTheInspectorDoes(Material material)
        {
            var editor = (MaterialEditor)UnityEditor.Editor.CreateEditor(material);
            try
            {
                editor.customShaderGUI?.ValidateMaterial(material);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(editor);
            }
        }

        private static void SetColor(Material material, string property, Color color)
        {
            if (material.HasProperty(property))
            {
                material.SetColor(property, color);
            }
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }
    }
}
