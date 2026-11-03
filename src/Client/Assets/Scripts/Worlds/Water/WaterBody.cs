using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    public enum WaterBodyKind
    {
        Lake = 0,
        River = 1,
        Sea = 2
    }

    // A lake, river, or sea in a World scene. Its mesh is the water surface: every triangle
    // faces up, and the surface may reach under the banks, where the terrain hides it. The
    // World collision bake reads the mesh below the collision root as the body's surface and
    // derives the deep water edges from it.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    public sealed class WaterBody : MonoBehaviour
    {
        [SerializeField] private string bodyId = string.Empty;
        [SerializeField] private WaterBodyKind kind;

        // Names the body for content such as fishing: lowercase letters, digits, dots,
        // dashes, and underscores, unique in its World.
        public string BodyId
        {
            get { return bodyId; }
        }

        public WaterBodyKind Kind
        {
            get { return kind; }
        }

        public Mesh Surface
        {
            get { return GetComponent<MeshFilter>().sharedMesh; }
        }

        public bool TryValidate(out string error)
        {
            if (!CollisionWaterSurface.IsValidBodyId(bodyId))
            {
                error = "The body id must be 1 to " + CollisionWaterSurface.MaximumBodyIdLength
                    + " lowercase letters, digits, dots, dashes, or underscores.";
                return false;
            }

            if (Surface == null)
            {
                error = "The MeshFilter has no surface mesh.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
