using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The materials player bodies draw with when they look different from their model: a copy
    // of a body material with another painted image, such as another face, a tint, or both,
    // shared by every body that looks the same. A property block on each body kept every body
    // out of the SRP Batcher, so a dense crowd paid a full draw call setup per player. The
    // owner destroys the copies.
    public sealed class PlayerBodyMaterials : IDisposable
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private readonly Dictionary<(Material Source, Texture BaseMap, Color Tint), Material> materials =
            new Dictionary<(Material Source, Texture BaseMap, Color Tint), Material>();

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
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (baseMap == null && tint == Color.white)
            {
                return source;
            }

            var key = (source, baseMap, tint);
            if (!materials.TryGetValue(key, out var material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name
                        + (baseMap != null ? " " + baseMap.name : string.Empty)
                        + (tint != Color.white ? " " + ColorUtility.ToHtmlStringRGB(tint) : string.Empty)
                };
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
