using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // One tinted copy of a body material per tint, shared by every player body with that
    // tint. A property block on each body kept every body out of the SRP Batcher, so a dense
    // crowd paid a full draw call setup per player. The owner destroys the copies.
    public sealed class PlayerBodyTintMaterials : IDisposable
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly Dictionary<(Material Source, Color Tint), Material> materials =
            new Dictionary<(Material Source, Color Tint), Material>();

        public int Count
        {
            get { return materials.Count; }
        }

        // A copy of the source whose base color is multiplied by the tint, so a painted
        // material keeps its texture.
        public Material For(Material source, Color tint)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var key = (source, tint);
            if (!materials.TryGetValue(key, out var material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name + " " + ColorUtility.ToHtmlStringRGB(tint)
                };
                if (source.HasProperty(BaseColorId))
                {
                    material.SetColor(BaseColorId, source.GetColor(BaseColorId) * tint);
                }

                if (source.HasProperty(ColorId))
                {
                    material.SetColor(ColorId, source.GetColor(ColorId) * tint);
                }

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
    }
}
