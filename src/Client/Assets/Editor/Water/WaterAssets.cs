using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // The textures and materials of the stylized water. Both are made here from code, so every
    // value of the look is reviewed with the code and a rebuild reproduces the same files: the
    // ripple normals, the masks for foam, light patterns, and sparkles, and the waterfall's
    // streaks from tileable noise, one material for every water surface, so water bodies that
    // meet draw alike, the waterfall's material, and the ring and the droplet of the wakes that
    // characters leave in the water. They live in their own folder, because the import rules of
    // the exported art own every texture below Assets/Art/Textures and import it as a color image.
    // A zone's own water colors are material variants of these materials, which a rebuild keeps.
    public static class WaterAssets
    {
        public const string MenuPath = "MMORPG/Tools/Water/Create Water Textures And Materials";
        public const string ShaderName = "Mmorpg/Water";
        public const string WaterfallShaderName = "Mmorpg/Waterfall";
        public const string ParticleShaderName = "Universal Render Pipeline/Particles/Unlit";
        public const string Directory = "Assets/Art/Water";
        public const string RipplesTexturePath = Directory + "/T_Water_Ripples.png";
        public const string MasksTexturePath = Directory + "/T_Water_Masks.png";
        public const string FallsTexturePath = Directory + "/T_Water_Falls.png";
        public const string RingTexturePath = Directory + "/T_Water_Ring.png";
        public const string DropletTexturePath = Directory + "/T_Water_Droplet.png";
        public const string SurfaceMaterialPath = Directory + "/M_Water.mat";
        public const string WaterfallMaterialPath = Directory + "/M_Water_Waterfall.mat";
        public const string RingMaterialPath = Directory + "/M_Water_Ring.mat";
        public const string SprayMaterialPath = Directory + "/M_Water_Spray.mat";

        private const int TexturePixels = 512;
        private const int RingPixels = 256;
        private const int DropletPixels = 64;

        [MenuItem(MenuPath)]
        public static void CreateFromMenu()
        {
            CreateOrUpdate();
            Debug.Log("[WATER] Created the water textures and the materials of every kind of water.");
        }

        // Writes the textures and the materials where they differ from what the code makes.
        public static void CreateOrUpdate()
        {
            EnsureFolder("Assets/Art", "Water");
            var ripples = WriteTexture(RipplesTexturePath, CreateRipplePixels(), true);
            var masks = WriteTexture(MasksTexturePath, CreateMaskPixels(), false);
            CreateOrUpdateMaterial(SurfaceMaterialPath, ShaderName, material =>
            {
                material.SetTexture("_NormalMap", ripples);
                material.SetTexture("_Masks", masks);
                ApplyLook(material);
            });

            var falls = WriteTexture(FallsTexturePath, CreateFallPixels(), false);
            CreateOrUpdateMaterial(WaterfallMaterialPath, WaterfallShaderName, material =>
            {
                material.SetTexture("_FallTexture", falls);
                material.SetTexture("_NormalMap", ripples);
                ApplyWaterfallLook(material);
            });

            var ring = WriteParticleTexture(RingTexturePath, CreateRingPixels(), RingPixels);
            var droplet = WriteParticleTexture(DropletTexturePath, CreateDropletPixels(), DropletPixels);
            CreateOrUpdateMaterial(RingMaterialPath, ParticleShaderName, material => ApplyParticleLook(material, ring));
            CreateOrUpdateMaterial(SprayMaterialPath, ParticleShaderName, material => ApplyParticleLook(material, droplet));
            AssetDatabase.SaveAssets();
        }

        // A white ring of foam, thick and broken in places, with a faint fill inside.
        private static Color32[] CreateRingPixels()
        {
            var pixels = new Color32[RingPixels * RingPixels];
            for (var y = 0; y < RingPixels; y++)
            {
                for (var x = 0; x < RingPixels; x++)
                {
                    var offsetX = (((x + 0.5f) / RingPixels) * 2f) - 1f;
                    var offsetY = (((y + 0.5f) / RingPixels) * 2f) - 1f;
                    var radius = Mathf.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
                    var band = Step(0.6f, 0.78f, radius) * (1f - Step(0.84f, 0.98f, radius));
                    var broken = 0.55f + (0.9f * TileableNoise.Gradient((x + 0.5f) / RingPixels, (y + 0.5f) / RingPixels, 6, 131));
                    var alpha = Mathf.Clamp01((band * Mathf.Clamp01(broken)) + (0.06f * (1f - Step(0.7f, 0.9f, radius))));
                    pixels[(y * RingPixels) + x] = new Color32(255, 255, 255, ToByte(alpha));
                }
            }

            return pixels;
        }

        // A soft round droplet.
        private static Color32[] CreateDropletPixels()
        {
            var pixels = new Color32[DropletPixels * DropletPixels];
            for (var y = 0; y < DropletPixels; y++)
            {
                for (var x = 0; x < DropletPixels; x++)
                {
                    var offsetX = (((x + 0.5f) / DropletPixels) * 2f) - 1f;
                    var offsetY = (((y + 0.5f) / DropletPixels) * 2f) - 1f;
                    var radius = Mathf.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
                    pixels[(y * DropletPixels) + x] = new Color32(255, 255, 255, ToByte(Mathf.Pow(1f - Step(0.2f, 1f, radius), 1.5f)));
                }
            }

            return pixels;
        }

        public static Material LoadMaterial(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new InvalidOperationException(
                    "The water material " + path + " is missing. Run " + MenuPath + ".");
            }

            return material;
        }

        // A white texture whose alpha draws the shape, clamped at its edges.
        private static Texture2D WriteParticleTexture(string path, Color32[] pixels, int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !BytesEqual(File.ReadAllBytes(fullPath), png))
            {
                File.WriteAllBytes(fullPath, png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer.textureType != TextureImporterType.Default
                || importer.wrapMode != TextureWrapMode.Clamp
                || !importer.mipmapEnabled
                || importer.alphaSource != TextureImporterAlphaSource.FromInput
                || !importer.alphaIsTransparency)
            {
                importer.textureType = TextureImporterType.Default;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // URP's unlit particle shader, alpha blended, one step after the water in the
        // transparent queue so the wakes always draw over it.
        private static void ApplyParticleLook(Material material, Texture2D texture)
        {
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_QueueOffset", 1f);
        }

        // The look goes onto a fresh material, validated as the shader's Inspector would, so no
        // property the code no longer sets stays behind, and the asset changes only when the
        // fresh material differs.
        private static void CreateOrUpdateMaterial(string path, string shaderName, Action<Material> applyLook)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                throw new InvalidOperationException("The shader " + shaderName + " is missing.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader)
                {
                    name = Path.GetFileNameWithoutExtension(path)
                };
                AssetDatabase.CreateAsset(material, path);
            }

            var target = new Material(shader)
            {
                name = material.name
            };
            try
            {
                applyLook(target);
                DevelopmentWorldPalette.ValidateAsTheInspectorDoes(target);
                if (EditorJsonUtility.ToJson(target) != EditorJsonUtility.ToJson(material))
                {
                    EditorUtility.CopySerialized(target, material);
                    EditorUtility.SetDirty(material);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        // Soft rolling swells and small ripples, as a height whose slopes become the normals.
        private static Color32[] CreateRipplePixels()
        {
            var heights = new float[TexturePixels * TexturePixels];
            for (var y = 0; y < TexturePixels; y++)
            {
                for (var x = 0; x < TexturePixels; x++)
                {
                    var u = (float)x / TexturePixels;
                    var v = (float)y / TexturePixels;
                    // Swells bent by slower noise, so their crests wander like wind on water,
                    // with small ripples on top.
                    var warpU = TileableNoise.Fractal(u, v, 3, 3, 211) * 0.12f;
                    var warpV = TileableNoise.Fractal(u, v, 3, 3, 223) * 0.12f;
                    var swell = TileableNoise.Fractal(u + warpU, v + warpV, 4, 5, 11);
                    var ripple = TileableNoise.Fractal(u, v, 12, 3, 37);
                    heights[(y * TexturePixels) + x] = swell + (0.25f * ripple);
                }
            }

            const float slopeScale = 24f;
            var pixels = new Color32[heights.Length];
            for (var y = 0; y < TexturePixels; y++)
            {
                for (var x = 0; x < TexturePixels; x++)
                {
                    var slopeX = (Height(heights, x + 1, y) - Height(heights, x - 1, y)) * 0.5f * slopeScale;
                    var slopeY = (Height(heights, x, y + 1) - Height(heights, x, y - 1)) * 0.5f * slopeScale;
                    var normal = new Vector3(-slopeX, -slopeY, 1f).normalized;
                    pixels[(y * TexturePixels) + x] = new Color32(
                        ToByte((normal.x * 0.5f) + 0.5f),
                        ToByte((normal.y * 0.5f) + 0.5f),
                        ToByte((normal.z * 0.5f) + 0.5f),
                        255);
                }
            }

            return pixels;
        }

        // Red is foam: low where foam forms first, a lace of thick lines between cells with
        // bubbles and soft clouds, so the shader grows foam from it as the amount rises. Green is
        // the light pattern of the shallows, bright lines between cells. Blue is soft noise that
        // places the sparkles.
        private static Color32[] CreateMaskPixels()
        {
            var pixels = new Color32[TexturePixels * TexturePixels];
            for (var y = 0; y < TexturePixels; y++)
            {
                for (var x = 0; x < TexturePixels; x++)
                {
                    var u = (float)x / TexturePixels;
                    var v = (float)y / TexturePixels;
                    TileableNoise.Cells(u, v, 5, 53, out var foamNearest, out var foamSecond);
                    TileableNoise.Cells(u, v, 11, 71, out var bubbleNearest, out _);
                    var lace = 1f - Step(0f, 0.32f, foamSecond - foamNearest);
                    var bubbles = 1f - Step(0.1f, 0.38f, bubbleNearest);
                    var cloud = Step(0.3f, 0.7f, (TileableNoise.Fractal(u, v, 3, 4, 89) * 0.5f) + 0.5f);
                    var foam = Mathf.Clamp01((0.55f * lace) + (0.3f * bubbles) + (0.45f * cloud));

                    TileableNoise.Cells(u, v, 7, 97, out var lightNearest, out var lightSecond);
                    var light = Mathf.Pow(1f - Step(0f, 0.14f, lightSecond - lightNearest), 1.6f);
                    var noise = (TileableNoise.Fractal(u, v, 8, 3, 113) * 0.5f) + 0.5f;
                    pixels[(y * TexturePixels) + x] = new Color32(ToByte(1f - foam), ToByte(light), ToByte(noise), 255);
                }
            }

            return pixels;
        }

        // Red is long thin streaks that run down a waterfall, green clumps of foam stretched the
        // same way, and blue fine noise that places its sparkles. The texture's height is the
        // direction of the fall.
        private static Color32[] CreateFallPixels()
        {
            var pixels = new Color32[TexturePixels * TexturePixels];
            for (var y = 0; y < TexturePixels; y++)
            {
                for (var x = 0; x < TexturePixels; x++)
                {
                    var u = (float)x / TexturePixels;
                    var v = (float)y / TexturePixels;
                    var streaks = Step(0.22f, 0.78f, (TileableNoise.Fractal(u, v, 24, 3, 4, 131) * 0.5f) + 0.5f);
                    var clumps = Step(0.56f, 0.8f, (TileableNoise.Fractal(u, v, 10, 3, 3, 149) * 0.5f) + 0.5f);
                    var sparkle = (TileableNoise.Fractal(u, v, 32, 2, 163) * 0.5f) + 0.5f;
                    pixels[(y * TexturePixels) + x] = new Color32(ToByte(streaks), ToByte(clumps), ToByte(sparkle), 255);
                }
            }

            return pixels;
        }

        // The shader smoothstep: 0 below the lower edge, 1 above the upper one, and a smooth
        // curve between. Unity's Mathf.SmoothStep interpolates between two values instead.
        private static float Step(float lower, float upper, float value)
        {
            var amount = Mathf.Clamp01((value - lower) / (upper - lower));
            return amount * amount * (3f - (2f * amount));
        }

        private static float Height(float[] heights, int x, int y)
        {
            x = (x + TexturePixels) % TexturePixels;
            y = (y + TexturePixels) % TexturePixels;
            return heights[(y * TexturePixels) + x];
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255f);
        }

        // Writes the PNG only when its bytes change, then imports it repeating, with mipmaps,
        // and as a normal map or as linear data.
        private static Texture2D WriteTexture(string path, Color32[] pixels, bool normalMap)
        {
            var texture = new Texture2D(TexturePixels, TexturePixels, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            var png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath) || !BytesEqual(File.ReadAllBytes(fullPath), png))
            {
                File.WriteAllBytes(fullPath, png);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var type = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != type
                || importer.sRGBTexture
                || importer.wrapMode != TextureWrapMode.Repeat
                || !importer.mipmapEnabled
                || importer.anisoLevel != 4
                || importer.filterMode != FilterMode.Trilinear
                || importer.alphaSource != TextureImporterAlphaSource.None
                || importer.textureCompression != TextureImporterCompression.CompressedHQ)
            {
                importer.textureType = type;
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.anisoLevel = 4;
                importer.filterMode = FilterMode.Trilinear;
                importer.alphaSource = TextureImporterAlphaSource.None;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Every property of the water surfaces' material. The water is turquoise and clear over
        // sand and deep blue further out, and a light wind keeps its ripples moving everywhere.
        // What sets a lake, a river, and the sea apart comes from their vertices: the flow, the
        // fast water that foams, the waves, and the foam lines that roll toward a beach.
        private static void ApplyLook(Material material)
        {
            material.SetColor("_ShallowColor", new Color(0.11f, 0.74f, 0.78f));
            material.SetColor("_DeepColor", new Color(0.025f, 0.19f, 0.47f));
            material.SetFloat("_DepthColorDistance", 2.8f);
            material.SetFloat("_Clarity", 2.6f);
            material.SetColor("_HorizonColor", new Color(0.62f, 0.84f, 0.95f));

            material.SetFloat("_NormalTiling", 6f);
            material.SetFloat("_NormalStrength", 0.85f);
            material.SetFloat("_DetailTiling", 1.6f);
            material.SetFloat("_DetailStrength", 0.47f);
            material.SetVector("_Drift", new Vector4(0.05f, 0.02f, 0f, 0f));
            material.SetFloat("_FlowCycle", 2.2f);
            material.SetVector("_RippleWind", new Vector4(0.15f, 0.09f, 0f, 0f));
            material.SetVector("_DetailWind", new Vector4(-0.13f, 0.26f, 0f, 0f));
            material.SetFloat("_RefractionStrength", 0.015f);
            material.SetFloat("_ReflectionStrength", 0.65f);
            material.SetFloat("_FresnelPower", 4f);
            material.SetFloat("_SpecularPower", 900f);
            material.SetFloat("_SpecularIntensity", 4f);
            material.SetFloat("_SheenIntensity", 0.5f);
            material.SetFloat("_SparkleIntensity", 1.5f);
            material.SetFloat("_RippleShading", 1.5f);

            material.SetColor("_FoamColor", new Color(0.92f, 0.97f, 1f));
            material.SetFloat("_FoamTiling", 7f);
            material.SetFloat("_FoamSoftness", 0.18f);
            material.SetFloat("_SurfacePattern", 0.12f);
            material.SetFloat("_SurfacePatternTiling", 5f);
            material.SetFloat("_ShoreFoamDepth", 0.38f);
            material.SetFloat("_IntersectionFoamDistance", 0.35f);
            material.SetFloat("_ShoreWaveFrequency", 1.2f);
            material.SetFloat("_ShoreWaveSpeed", 0.18f);
            material.SetFloat("_ShoreWaveDepth", 1.6f);
            material.SetFloat("_TurbulenceFoam", 0.8f);

            material.SetFloat("_CausticsTiling", 2.6f);
            material.SetFloat("_CausticsStrength", 0.7f);
            material.SetFloat("_CausticsDepth", 2.5f);

            material.SetFloat("_WaveAmplitude", 0.12f);
            material.SetFloat("_WaveLength", 14f);
            material.SetFloat("_WaveSpeed", 2f);
            material.SetFloat("_EdgeFade", 0.2f);
        }

        // The colors follow the lake's, lighter, because a falling sheet is thin, and the light
        // follows the water surfaces: the same horizon, glints, and foam.
        private static void ApplyWaterfallLook(Material material)
        {
            material.SetColor("_Color", new Color(0.36f, 0.82f, 0.86f));
            material.SetColor("_DeepColor", new Color(0.06f, 0.36f, 0.52f));
            material.SetColor("_FoamColor", new Color(0.92f, 0.97f, 1f));
            material.SetColor("_HorizonColor", new Color(0.62f, 0.84f, 0.95f));
            material.SetFloat("_PatternSpeed", 3.2f);
            material.SetFloat("_StreakWidth", 2.4f);
            material.SetFloat("_StreakLength", 4f);
            material.SetFloat("_NormalStrength", 0.8f);
            material.SetFloat("_Foam", 0.42f);
            material.SetFloat("_LipGlass", 0.18f);
            material.SetFloat("_FootFoam", 0.3f);
            material.SetFloat("_Opacity", 0.55f);
            material.SetFloat("_RefractionStrength", 0.035f);
            material.SetFloat("_ReflectionStrength", 0.55f);
            material.SetFloat("_SpecularPower", 300f);
            material.SetFloat("_SpecularIntensity", 3f);
            material.SetFloat("_SparkleIntensity", 2f);
            material.SetFloat("_OutlineSoftness", 0.3f);
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + child))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
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
    }
}
