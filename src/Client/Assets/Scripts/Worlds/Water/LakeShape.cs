using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // A lake: still water at the height of its transform inside its shore, a closed outline in
    // the transform's X and Z. The surface reaches past the shore under the bank, where the
    // terrain hides it, and churns where a waterfall lands in it. Below it the floor falls from
    // the shore to its deepest, and the bank rises from the shore to the ground around it.
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

        [Tooltip("How much deeper the floor gets per meter from the shore (m per m).")]
        [SerializeField, Min(0f)] private float floorSlope = 0.3f;

        [Tooltip("The deepest the lake gets below its surface (m).")]
        [SerializeField, Min(0f)] private float maximumDepth = 3f;

        [Tooltip("Over how far past the shore the bank rises to the ground around the lake (m).")]
        [SerializeField, Min(0f)] private float bankWidth = 2f;

        public IReadOnlyList<Vector2> Shore
        {
            get { return shore; }
        }

        // At least as large as the Inspector allows, also when a script sets it.
        public float CellSize
        {
            get { return Mathf.Max(0.25f, cellSize); }
        }

        public float FloorSlope
        {
            get { return Mathf.Max(0f, floorSlope); }
        }

        public float MaximumDepth
        {
            get { return Mathf.Max(0f, maximumDepth); }
        }

        public override float BankWidth
        {
            get { return Mathf.Max(0f, bankWidth); }
        }

        // The lake's surface height in world space.
        public float Level
        {
            get { return transform.position.y; }
        }
    }
}
