using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // One point of a river's center line in its transform's space: the height of the water's
    // surface there, and how wide, deep, fast, and foaming the river is.
    [Serializable]
    public struct RiverPoint
    {
        [Tooltip("The center of the river's surface in the transform's space (m).")]
        public Vector3 Position;

        [Tooltip("The width of the river's water from bank to bank (m).")]
        [Min(0.5f)] public float Width;

        [Tooltip("The depth of the water in the middle (m).")]
        [Min(0.05f)] public float Depth;

        [Tooltip("How fast the water flows in the middle (m/s).")]
        [Min(0f)] public float Speed;

        [Tooltip("How much the water foams, from 0 to 1.")]
        [Range(0f, 1f)] public float Foam;

        public RiverPoint(Vector3 position, float width, float depth, float speed, float foam)
        {
            Position = position;
            Width = width;
            Depth = depth;
            Speed = speed;
            Foam = foam;
        }
    }

    // A river: water that flows from its first point to its last along a center line through
    // its points, and falls evenly in height between them. Where it starts or ends in a lake or
    // a sea, its surface lies at their level and stands still where they overlap, so they meet
    // without a seam. A waterfall can fall from its last point.
    [DisallowMultipleComponent]
    public sealed class RiverShape : WaterSurfaceShape
    {
        [Tooltip("The center line from where the river starts to where it ends.")]
        [SerializeField] private List<RiverPoint> points = new List<RiverPoint>
        {
            new RiverPoint(new Vector3(0f, 0f, -10f), 6f, 1f, 0.8f, 0f),
            new RiverPoint(new Vector3(0f, 0f, 10f), 6f, 1f, 0.8f, 0f)
        };

        [Tooltip("A curve through the points; otherwise straight lines between them.")]
        [SerializeField] private bool curved = true;

        [Tooltip("The distance between the rows of the surface along the river (m).")]
        [SerializeField, Min(0.1f)] private float rowSpacing = 1f;

        [Tooltip("How much slower the water flows at the banks than in the middle, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float edgeSlowdown = 0.7f;

        public IReadOnlyList<RiverPoint> Points
        {
            get { return points; }
        }

        public bool Curved
        {
            get { return curved; }
        }

        public float RowSpacing
        {
            get { return rowSpacing; }
        }

        public float EdgeSlowdown
        {
            get { return edgeSlowdown; }
        }
    }
}
