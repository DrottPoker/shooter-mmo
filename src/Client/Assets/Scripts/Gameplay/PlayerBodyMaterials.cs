using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The materials player bodies draw with when they look different from their model: a copy
    // of a body material with another painted image, such as another face, paint layers over
    // its skin, such as a tattoo or underwear, a tint, or any of them together, shared by
    // every body that looks the same. A property block on each body kept every body
    // out of the SRP Batcher, so a dense crowd paid a full draw call setup per player. The
    // owner destroys the copies.
    public sealed class PlayerBodyMaterials : IDisposable
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int PaintCountId = Shader.PropertyToID("_PaintCount");
        private static readonly int[] PaintMapIds = PaintIds("_PaintMap");
        private static readonly int[] PaintSurfaceIds = PaintIds("_PaintSurface");
        private static readonly int[] PaintRectIds = PaintIds("_PaintRect");

        private static readonly IReadOnlyList<BodyAppearanceCatalog.Paint> NoPaints =
            new BodyAppearanceCatalog.Paint[0];

        private readonly Dictionary<(Material Source, Texture BaseMap, Color Tint, string Paints), Material> materials =
            new Dictionary<(Material Source, Texture BaseMap, Color Tint, string Paints), Material>();

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
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            paints = paints != null && source.HasProperty(PaintCountId) ? paints : NoPaints;
            if (baseMap == null && tint == Color.white && paints.Count == 0)
            {
                return source;
            }

            var paintKey = PaintKeyOf(paints);
            var key = (source, baseMap, tint, paintKey);
            if (!materials.TryGetValue(key, out var material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name
                        + (baseMap != null ? " " + baseMap.name : string.Empty)
                        + (paintKey.Length > 0 ? " " + paintKey : string.Empty)
                        + (tint != Color.white ? " " + ColorUtility.ToHtmlStringRGB(tint) : string.Empty)
                };
                SetPaints(material, paints);
                if (baseMap != null)
                {
                    SetTexture(material, BaseMapId, baseMap);
                    SetTexture(material, MainTexId, baseMap);
                }

                MultiplyColor(material, source, BaseColorId, tint);
                MultiplyColor(material, source, ColorId, tint);
                materials[key] = material;
            }

            return material;
        }

        public void Dispose()
        {
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
    }
}
