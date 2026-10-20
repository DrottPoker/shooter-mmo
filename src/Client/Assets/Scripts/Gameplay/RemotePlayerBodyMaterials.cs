using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // One tinted copy of a body material per color, shared by every remote player of that
    // color. A property block on each body kept every remote body out of the SRP Batcher, so a
    // dense crowd paid a full draw call setup per player. The owner destroys the copies.
    public sealed class RemotePlayerBodyMaterials : IDisposable
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private readonly Dictionary<(Material Source, Color Color), Material> materials =
            new Dictionary<(Material Source, Color Color), Material>();

        public int Count
        {
            get { return materials.Count; }
        }

        public Material For(Material source, Color color)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var key = (source, color);
            if (!materials.TryGetValue(key, out var material) || material == null)
            {
                material = new Material(source)
                {
                    name = source.name + " " + ColorUtility.ToHtmlStringRGB(color)
                };
                material.SetColor(BaseColorId, color);
                material.SetColor(ColorId, color);
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
