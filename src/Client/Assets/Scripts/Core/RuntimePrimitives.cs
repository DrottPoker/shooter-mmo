using UnityEngine;

namespace Mmorpg.Client.Core
{
    // Creates the primitives that runtime presentation builds itself, such as the destination
    // marker, flying arrows, and fallback models, with the authored material from the client
    // configuration. A player build has no default material for the render pipeline, so a bare
    // GameObject.CreatePrimitive gets the built-in Standard shader, which URP draws as its pink
    // error shader. Only the Editor hides that.
    public static class RuntimePrimitives
    {
        public static GameObject Create(PrimitiveType type, Material material)
        {
            var primitive = GameObject.CreatePrimitive(type);
            var renderer = primitive.GetComponent<Renderer>();
            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
            }

            return primitive;
        }
    }
}
