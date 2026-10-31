using System.Collections.Generic;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The kinds of collision the Debug menu draws, each in its own color.
    public enum CollisionDebugKind
    {
        // Triangles that face up within the walkable slope.
        WalkableGround = 0,

        // Triangles too steep to stand on, such as cliffs and walls of meshes.
        SteepSurface = 1,

        // Boxes that stop movement and line of sight, such as walls and rocks.
        Obstacle = 2,

        // Boxes that stop movement only, such as the bounds of a resource node.
        MovementOnly = 3,

        // Boxes that stop line of sight only.
        CombatOnly = 4
    }

    // Turns the collision near a point into line segments by kind, two points per segment: the
    // edges of every triangle, lifted a little along its normal so the drawn ground does not hide
    // them, and the twelve edges of every box. It reads the same baked collision that the
    // planner, the prediction, and the worker use.
    public sealed class CollisionDebugLines
    {
        public const int KindCount = 5;

        // How far above its triangle a ground line is drawn.
        public const float TriangleLift = 0.03f;

        // Collision this far above or below the center still shows.
        private const float VerticalReach = 64f;

        private readonly CollisionQueryBuffer buffer = new CollisionQueryBuffer();
        private readonly List<Vector3>[] segments = new List<Vector3>[KindCount];
        private readonly Vector3[] corners = new Vector3[8];

        public CollisionDebugLines()
        {
            for (var kind = 0; kind < KindCount; kind++)
            {
                segments[kind] = new List<Vector3>();
            }
        }

        // The shapes of the last build.
        public int ShapeCount { get; private set; }

        public IReadOnlyList<Vector3> Segments(CollisionDebugKind kind)
        {
            return segments[(int)kind];
        }

        // Collects every shape whose bounds reach into the square of half size reach around the
        // center.
        public void Build(ICollisionWorld world, Vector3 center, float reach, float maximumSlopeDegrees)
        {
            for (var kind = 0; kind < KindCount; kind++)
            {
                segments[kind].Clear();
            }

            ShapeCount = 0;
            if (world == null)
            {
                return;
            }

            buffer.Clear();
            world.QueryShapes(
                new CollisionAabb(
                    new SimulationVector3(center.x - reach, center.y - VerticalReach, center.z - reach),
                    new SimulationVector3(center.x + reach, center.y + VerticalReach, center.z + reach)),
                CollisionLayers.All,
                buffer);
            var minimumGroundNormalY = Mathf.Cos(maximumSlopeDegrees * Mathf.Deg2Rad);
            var shapes = buffer.Shapes;
            ShapeCount = shapes.Count;
            for (var index = 0; index < shapes.Count; index++)
            {
                switch (shapes[index])
                {
                    case CollisionTriangle triangle:
                        AddTriangle(triangle, minimumGroundNormalY);
                        break;
                    case CollisionBox box:
                        AddBox(box);
                        break;
                }
            }

            buffer.Clear();
        }

        public static CollisionDebugKind KindOf(CollisionBox box)
        {
            var movement = (box.LayerMask & CollisionLayers.CharacterMovement) != 0;
            var combat = (box.LayerMask & CollisionLayers.CombatQueries) != 0;
            if (movement && combat)
            {
                return CollisionDebugKind.Obstacle;
            }

            return movement ? CollisionDebugKind.MovementOnly : CollisionDebugKind.CombatOnly;
        }

        private void AddTriangle(CollisionTriangle triangle, float minimumGroundNormalY)
        {
            var normal = ToVector(triangle.Normal);
            var lift = normal * TriangleLift;
            var a = ToVector(triangle.A) + lift;
            var b = ToVector(triangle.B) + lift;
            var c = ToVector(triangle.C) + lift;
            var lines = segments[(int)(normal.y >= minimumGroundNormalY
                ? CollisionDebugKind.WalkableGround
                : CollisionDebugKind.SteepSurface)];
            AddSegment(lines, a, b);
            AddSegment(lines, b, c);
            AddSegment(lines, c, a);
        }

        private void AddBox(CollisionBox box)
        {
            var lines = segments[(int)KindOf(box)];
            var center = box.Center;
            var half = box.HalfExtents;
            for (var corner = 0; corner < 8; corner++)
            {
                var local = new SimulationVector3(
                    (corner & 1) == 0 ? -half.X : half.X,
                    (corner & 2) == 0 ? -half.Y : half.Y,
                    (corner & 4) == 0 ? -half.Z : half.Z);
                corners[corner] = ToVector(center + box.Rotation.Rotate(local));
            }

            // The corners differ in one bit per axis, so every edge joins two corners whose
            // indices differ in exactly one bit.
            for (var corner = 0; corner < 8; corner++)
            {
                for (var bit = 1; bit < 8; bit <<= 1)
                {
                    if ((corner & bit) == 0)
                    {
                        AddSegment(lines, corners[corner], corners[corner | bit]);
                    }
                }
            }
        }

        private static void AddSegment(List<Vector3> lines, Vector3 from, Vector3 to)
        {
            lines.Add(from);
            lines.Add(to);
        }

        private static Vector3 ToVector(SimulationVector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }
    }
}
