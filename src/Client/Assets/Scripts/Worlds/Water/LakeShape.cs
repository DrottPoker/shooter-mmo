using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A lake: still water at the height of its transform inside its shore, a closed outline in
    // the transform's X and Z. The surface reaches past the shore under the bank, where the
    // terrain hides it, and churns where a waterfall lands in it.
    [DisallowMultipleComponent]
    public sealed class LakeShape : WaterSurfaceShape
    {
        [Tooltip("The shore, a closed outline in the transform's X and Z (m).")]
        [SerializeField] private List<Vector2> shore = new List<Vector2>
        {
            new Vector2(-8f, -6f),
            new Vector2(-8f, 6f),
            new Vector2(8f, 6f),
            new Vector2(8f, -6f)
        };

        [Tooltip("The size of the surface's grid cells (m).")]
        [SerializeField, Min(0.25f)] private float cellSize = 1f;

        public IReadOnlyList<Vector2> Shore
        {
            get { return shore; }
        }

        public float CellSize
        {
            get { return cellSize; }
        }

        // The lake's surface height in world space.
        public float Level
        {
            get { return transform.position.y; }
        }
    }
}
