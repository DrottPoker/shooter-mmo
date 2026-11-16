using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The materials player bodies draw with when they look different from their model: a copy
    // of a body material with another painted image, such as another face, paint layers over
    // its skin, such as a tattoo or underwear, a tint, a garment's dye colors, or any of them
    // together, shared by every body that looks the same. A property block on each body kept every
    // body out of the SRP Batcher, so a dense crowd paid a full draw call setup per player. The
    // bodies and corpses that show a copy count as its users; a copy nobody uses stays
    // PlayerBodyMeshes.GraceSeconds, at most UnusedCap of them, and is then destroyed. The owner
    // destroys the rest.
    public sealed class PlayerBodyMaterials : IDisposable
    {
        // Copies are small, so more of them stay unused than meshes do.
        public const int UnusedCap = 64;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int PaintCountId = Shader.PropertyToID("_PaintCount");
        private static readonly int[] PaintMapIds = PaintIds("_PaintMap");
        private static readonly int[] PaintSurfaceIds = PaintIds("_PaintSurface");
        private static readonly int[] PaintRectIds = PaintIds("_PaintRect");
        private static readonly int DyeCountId = Shader.PropertyToID("_DyeCount");
        private static readonly int[] DyeColorIds = DyeIds();

        private static readonly IReadOnlyList<BodyAppearanceCatalog.Paint> NoPaints =
            new BodyAppearanceCatalog.Paint[0];

        private static readonly IReadOnlyList<Color> NoDyes = new Color[0];

        private readonly Dictionary<(Material Source, Texture BaseMap, Color Tint, string Paints, string Dyes), Material> materials =
            new Dictionary<(Material Source, Texture BaseMap, Color Tint, string Paints, string Dyes), Material>();

        // Every copy with its key and users, and the copies without users in the order they lost
        // their last one.
        private readonly Dictionary<Material, Copy> copies = new Dictionary<Material, Copy>();
        private readonly List<Copy> unused = new List<Copy>();
        private bool disposed;

        // The dye channels the garment shader, Mmorpg/Garment, has.
        public const int MaximumDyes = 3;

        // The paint layers the skin shader, Mmorpg/Character Skin, draws over its base map.
        public const int MaximumPaintLayers = 4;

        public int Count
        {
            get { return materials.Count; }
        }

        // A tinted copy of the source, which keeps its texture.
        public Material For(Material source, Color tint)
        {
            return For(source, null, tint);
        }

        // The source with another base map, or its own when baseMap is null, whose base color
        // is multiplied by the tint. The source itself when nothing differs.
        public Material For(Material source, Texture baseMap, Color tint)
        {
            return For(source, baseMap, tint, NoPaints);
        }

        // Also with paint layers over the base map, in the order they are drawn, when the source
        // draws with the skin shader; another shader ignores them.
        public Material For(Material source, Texture baseMap, Color tint, IReadOnlyList<BodyAppearanceCatalog.Paint> paints)
        {
            return For(source, baseMap, tint, paints, NoDyes);
        }

        // Also with the colors of a garment's dye channels in place of its natural colors, when the
        // source draws with the garment shader and has those channels; colors beyond its channels
        // are left out.
        public Material For(Material source, Texture baseMap, Color tint, IReadOnlyList<BodyAppearanceCatalog.Paint> paints,
            IReadOnlyList<Color> dyes)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            paints = paints != null && source.HasProperty(PaintCountId) ? paints : NoPaints;
            var dyeKey = DyeKeyOf(source, dyes);
            if (baseMap == null && tint == Color.white && paints.Count == 0 && dyeKey.Length == 0)
            {
                return source;
            }

            var paintKey = PaintKeyOf(paints);
            var key = (source, baseMap, tint, paintKey, dyeKey);
            if (!materials.TryGetValue(key, out var material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name
                        + (baseMap != null ? " " + baseMap.name : string.Empty)
                        + (paintKey.Length > 0 ? " " + paintKey : string.Empty)
                        + (dyeKey.Length > 0 ? " " + dyeKey : string.Empty)
                        + (tint != Color.white ? " " + ColorUtility.ToHtmlStringRGB(tint) : string.Empty)
                };
                SetPaints(material, paints);
                SetDyes(material, dyes, dyeKey);
                if (baseMap != null)
                {
                    SetTexture(material, BaseMapId, baseMap);
                    SetTexture(material, MainTexId, baseMap);
                }

                MultiplyColor(material, source, BaseColorId, tint);
                MultiplyColor(material, source, ColorId, tint);
                materials[key] = material;
                var copy = new Copy(key, material) { UnusedSince = Time.realtimeSinceStartup };
                copies[material] = copy;
                unused.Add(copy);
            }

            return material;
        }

        // A body or corpse shows the material until it releases it; a material that is no copy of
        // this set, such as a body's own, is not counted.
        public void AddUser(Material material)
        {
            if (material == null || !copies.TryGetValue(material, out var copy))
            {
                return;
            }

            if (copy.Users == 0)
            {
                unused.Remove(copy);
            }

            copy.Users++;
        }

        public void Release(Material material)
        {
            if (disposed || material == null || !copies.TryGetValue(material, out var copy) || copy.Users == 0)
            {
                return;
            }

            copy.Users--;
            if (copy.Users == 0)
            {
                copy.UnusedSince = Time.realtimeSinceStartup;
                unused.Add(copy);
            }
        }

        // Destroys the copies without users past the grace time and the oldest beyond the cap.
        // Allocates nothing.
        public void Update()
        {
            Trim(Time.realtimeSinceStartup);
        }

        // Destroys every copy without a user at once.
        public void ReleaseUnused()
        {
            Trim(float.PositiveInfinity);
        }

        internal void Trim(float now)
        {
            for (var index = unused.Count - 1; index >= 0; index--)
            {
                if (now - unused[index].UnusedSince >= PlayerBodyMeshes.GraceSeconds)
                {
                    Forget(index);
                }
            }

            while (unused.Count > UnusedCap)
            {
                Forget(0);
            }
        }

        private void Forget(int unusedIndex)
        {
            var copy = unused[unusedIndex];
            unused.RemoveAt(unusedIndex);
            copies.Remove(copy.Material);
            materials.Remove(copy.Key);
            DestroyMaterial(copy.Material);
        }

        private static void DestroyMaterial(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(material);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        public void Dispose()
        {
            disposed = true;
            copies.Clear();
            unused.Clear();
            foreach (var material in materials.Values)
            {
                if (material == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(material);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            materials.Clear();
        }

        private static int[] PaintIds(string name)
        {
            var ids = new int[MaximumPaintLayers];
            for (var index = 0; index < ids.Length; index++)
            {
                ids[index] = Shader.PropertyToID(name + (index + 1));
            }

            return ids;
        }

        private static int[] DyeIds()
        {
            var ids = new int[MaximumDyes];
            for (var index = 0; index < ids.Length; index++)
            {
                ids[index] = Shader.PropertyToID("_DyeColor" + (index + 1));
            }

            return ids;
        }

        // The dye colors the source takes, RRGGBB joined by dots, empty when it takes none: a
        // material without dye channels, or colors equal to its natural ones, keeps its own.
        private static string DyeKeyOf(Material source, IReadOnlyList<Color> dyes)
        {
            if (dyes == null || dyes.Count == 0 || !source.HasProperty(DyeCountId))
            {
                return string.Empty;
            }

            var count = Mathf.Min(dyes.Count, Mathf.Min(MaximumDyes, Mathf.RoundToInt(source.GetFloat(DyeCountId))));
            var differs = false;
            var key = string.Empty;
            for (var index = 0; index < count; index++)
            {
                var color = ColorUtility.ToHtmlStringRGB(dyes[index]);
                differs |= color != ColorUtility.ToHtmlStringRGB(source.GetColor(DyeColorIds[index]));
                key += (index > 0 ? "." : string.Empty) + color;
            }

            return differs ? key : string.Empty;
        }

        private static void SetDyes(Material material, IReadOnlyList<Color> dyes, string dyeKey)
        {
            if (dyeKey.Length == 0)
            {
                return;
            }

            var count = dyeKey.Split('.').Length;
            for (var index = 0; index < count; index++)
            {
                material.SetColor(DyeColorIds[index], dyes[index]);
            }
        }

        private static string PaintKeyOf(IReadOnlyList<BodyAppearanceCatalog.Paint> paints)
        {
            var key = string.Empty;
            for (var index = 0; index < paints.Count && index < MaximumPaintLayers; index++)
            {
                key += (index > 0 ? "+" : string.Empty) + paints[index].Name;
            }

            return key;
        }

        // The first layers the shader can draw; a look with more paint layers than that is
        // reported once per material.
        private static void SetPaints(Material material, IReadOnlyList<BodyAppearanceCatalog.Paint> paints)
        {
            if (paints.Count == 0)
            {
                return;
            }

            if (paints.Count > MaximumPaintLayers)
            {
                Debug.LogWarning(material.name + " has " + paints.Count + " paint layers; the skin shader draws the first "
                    + MaximumPaintLayers + ".");
            }

            var count = Mathf.Min(paints.Count, MaximumPaintLayers);
            for (var index = 0; index < count; index++)
            {
                material.SetTexture(PaintMapIds[index], paints[index].Texture);
                material.SetVector(PaintSurfaceIds[index], new Vector4(paints[index].Smoothness, paints[index].Metallic, 0f, 0f));
                material.SetVector(PaintRectIds[index], paints[index].Rect);
            }

            material.SetFloat(PaintCountId, count);
        }

        private static void SetTexture(Material material, int property, Texture texture)
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
            }
        }

        private static void MultiplyColor(Material material, Material source, int property, Color tint)
        {
            if (source.HasProperty(property))
            {
                material.SetColor(property, source.GetColor(property) * tint);
            }
        }

        private sealed class Copy
        {
            public Copy((Material Source, Texture BaseMap, Color Tint, string Paints, string Dyes) key, Material material)
            {
                Key = key;
                Material = material;
            }

            public (Material Source, Texture BaseMap, Color Tint, string Paints, string Dyes) Key { get; }

            public Material Material { get; }

            public int Users { get; set; }

            public float UnusedSince { get; set; }
        }
    }
}
