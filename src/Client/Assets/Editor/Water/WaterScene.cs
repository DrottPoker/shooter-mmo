using System.Collections.Generic;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Editor
{
    // The water shapes of a scene in world space and how they meet: which lake or sea a river
    // starts or ends in, which river a waterfall falls from and where it lands, and where the
    // World's ground ends, beyond which a sea runs on to the horizon. The water builders read it,
    // so a shape never has to repeat what another shape already says.
    internal sealed class WaterScene
    {
        // A river that starts or ends at most this far above or below a lake or sea meets it.
        public const float JunctionLevelTolerance = 0.5f;

        // A waterfall falls at least this far from its lip to its landing (m).
        public const float MinimumFall = 0.1f;

        private WaterScene()
        {
        }

        public List<LakeModel> Lakes { get; } = new List<LakeModel>();

        public List<SeaModel> Seas { get; } = new List<SeaModel>();

        public List<RiverModel> Rivers { get; } = new List<RiverModel>();

        public List<WaterfallModel> Waterfalls { get; } = new List<WaterfallModel>();

        // The shapes that make nothing until they are complete: a river with fewer than two
        // points, and a waterfall without a river or a landing below its lip, or from a river that
        // another waterfall already falls from.
        public List<WorldBuilderShape> Incomplete { get; } = new List<WorldBuilderShape>();

        // Where the World's ground ends in X and Z, or null without any ground.
        public Rect? WorldEdge { get; private set; }

        public static WaterScene Collect(Scene scene)
        {
            var shapes = new List<WorldBuilderShape>();
            foreach (var root in scene.GetRootGameObjects())
            {
                shapes.AddRange(root.GetComponentsInChildren<WorldBuilderShape>(false));
            }

            return Collect(shapes, GroundEdge(scene));
        }

        public static WaterScene Collect(IEnumerable<WorldBuilderShape> shapes, Rect? worldEdge)
        {
            var water = new WaterScene { WorldEdge = worldEdge };
            var waterfalls = new List<WaterfallShape>();
            foreach (var shape in shapes)
            {
                if (shape == null || !shape.enabled || !shape.gameObject.activeInHierarchy)
                {
                    continue;
                }

                switch (shape)
                {
                    case LakeShape lake:
                        water.Lakes.Add(new LakeModel(lake));
                        break;
                    case SeaShape sea:
                        water.Seas.Add(new SeaModel(sea));
                        break;
                    case RiverShape river when river.Points.Count >= 2:
                        water.Rivers.Add(new RiverModel(river));
                        break;
                    case RiverShape river:
                        water.Incomplete.Add(river);
                        break;
                    case WaterfallShape waterfall:
                        waterfalls.Add(waterfall);
                        break;
                }
            }

            foreach (var river in water.Rivers)
            {
                river.Start = water.FindJunction(river.Path.ValuesAt(0f).Position);
                river.End = water.FindJunction(river.Path.ValuesAt(river.Path.Length).Position);
                if (river.End != null && river.End.Body is SeaModel sea)
                {
                    sea.Mouths.Add(river);
                }
            }

            foreach (var waterfall in waterfalls)
            {
                var river = water.Rivers.Find(model => model.River == waterfall.River);
                var landing = water.FindSurface(waterfall.Landing);
                if (river == null
                    || landing == null
                    || landing == river
                    || river.Fall != null
                    || river.Path.ValuesAt(river.Path.Length).Position.y - landing.Level < MinimumFall)
                {
                    water.Incomplete.Add(waterfall);
                    continue;
                }

                var model = new WaterfallModel(waterfall, river, landing);
                river.Fall = model;
                landing.Landings.Add(model);
                water.Waterfalls.Add(model);
            }

            return water;
        }

        // The lake or sea whose surface covers a point at about its height, the highest if several do.
        public SurfaceModel FindBodyAt(Vector3 point)
        {
            SurfaceModel best = null;
            foreach (var body in OpenWaters())
            {
                if (Mathf.Abs(body.Level - point.y) <= JunctionLevelTolerance
                    && body.Covers(new Vector2(point.x, point.z))
                    && (best == null || body.Level > best.Level))
                {
                    best = body;
                }
            }

            return best;
        }

        public SurfaceModel FindSurface(WaterBody body)
        {
            if (body == null)
            {
                return null;
            }

            foreach (var lake in Lakes)
            {
                if (lake.Shape.Body == body)
                {
                    return lake;
                }
            }

            foreach (var sea in Seas)
            {
                if (sea.Shape.Body == body)
                {
                    return sea;
                }
            }

            return Rivers.Find(river => river.Shape.Body == body);
        }

        private IEnumerable<OpenWaterModel> OpenWaters()
        {
            foreach (var lake in Lakes)
            {
                yield return lake;
            }

            foreach (var sea in Seas)
            {
                yield return sea;
            }
        }

        private RiverJunction FindJunction(Vector3 point)
        {
            return FindBodyAt(point) is OpenWaterModel body ? new RiverJunction(body) : null;
        }

        // The ground of a World is what its colliders cover: the collision root's, or the scene's
        // when it has no collision root.
        private static Rect? GroundEdge(Scene scene)
        {
            Bounds? ground = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var authoring = root.GetComponentInChildren<WorldCollisionAuthoring>(true);
                var colliderRoot = authoring != null && authoring.CollisionRoot != null ? authoring.CollisionRoot : root.transform;
                foreach (var collider in colliderRoot.GetComponentsInChildren<Collider>(false))
                {
                    if (!collider.enabled || collider.isTrigger)
                    {
                        continue;
                    }

                    var bounds = collider.bounds;
                    if (bounds.size == Vector3.zero)
                    {
                        continue;
                    }

                    if (ground.HasValue)
                    {
                        var merged = ground.Value;
                        merged.Encapsulate(bounds);
                        ground = merged;
                    }
                    else
                    {
                        ground = bounds;
                    }
                }
            }

            return ground.HasValue
                ? Rect.MinMaxRect(ground.Value.min.x, ground.Value.min.z, ground.Value.max.x, ground.Value.max.z)
                : (Rect?)null;
        }
    }

    // A water body whose surface a shape makes, in world space.
    internal abstract class SurfaceModel
    {
        protected SurfaceModel(WaterSurfaceShape shape)
        {
            Shape = shape;
        }

        public WaterSurfaceShape Shape { get; }

        // The waterfalls that land in this water.
        public List<WaterfallModel> Landings { get; } = new List<WaterfallModel>();

        public abstract float Level { get; }

        // Whether the drawn surface, under the banks included, covers the point in X and Z.
        public abstract bool Covers(Vector2 point);

        protected static Vector2[] ToWorld(Transform transform, IReadOnlyList<Vector2> local)
        {
            var world = new Vector2[local.Count];
            for (var index = 0; index < local.Count; index++)
            {
                var point = transform.TransformPoint(new Vector3(local[index].x, 0f, local[index].y));
                world[index] = new Vector2(point.x, point.z);
            }

            return world;
        }
    }

    // Still or open water at one level inside an outline: a lake or a sea.
    internal abstract class OpenWaterModel : SurfaceModel
    {
        // Half the diagonal of a square cell: how far a cell whose center lies within the reach
        // can reach beyond it.
        private const float HalfDiagonal = 0.7072f;

        private readonly float rim;

        protected OpenWaterModel(WaterSurfaceShape shape, IReadOnlyList<Vector2> outline, float cellSize)
            : base(shape)
        {
            Outline = ToWorld(shape.transform, outline);
            rim = shape.BankReach + (cellSize * HalfDiagonal);
        }

        // The shore or the sea's outline in world X and Z.
        public Vector2[] Outline { get; }

        public override float Level
        {
            get { return Shape.transform.position.y; }
        }

        // The grid's cells reach the bank reach beyond the outline, and their jagged rim up to
        // half a cell's diagonal further.
        public override bool Covers(Vector2 point)
        {
            return Outline.Length >= 3
                && (WorldBuilderGeometry.Contains(Outline, point)
                    || WorldBuilderGeometry.DistanceToOutline(Outline, point) <= rim);
        }
    }

    internal sealed class LakeModel : OpenWaterModel
    {
        public LakeModel(LakeShape lake)
            : base(lake, lake.Shore, lake.CellSize)
        {
            Lake = lake;
        }

        public LakeShape Lake { get; }
    }

    internal sealed class SeaModel : OpenWaterModel
    {
        public SeaModel(SeaShape sea)
            : base(sea, sea.Outline, sea.CellSize)
        {
            Sea = sea;
        }

        public SeaShape Sea { get; }

        // The rivers that flow into the sea.
        public List<RiverModel> Mouths { get; } = new List<RiverModel>();
    }

    internal sealed class RiverModel : SurfaceModel
    {
        // Rows of the surface along a river that are not covered by the water it meets ease from
        // still to flowing over this length (m).
        public const float JunctionLength = 6f;

        public RiverModel(RiverShape river)
            : base(river)
        {
            River = river;
            var points = new RiverPoint[river.Points.Count];
            for (var index = 0; index < points.Length; index++)
            {
                var point = river.Points[index];
                point.Position = river.transform.TransformPoint(point.Position);
                points[index] = point;
            }

            Path = new RiverPath(points, river.Curved);
        }

        public RiverShape River { get; }

        public RiverPath Path { get; }

        // The lake or sea the river starts in, or null.
        public RiverJunction Start { get; set; }

        // The lake or sea the river ends in, or null.
        public RiverJunction End { get; set; }

        // The waterfall that falls from the river's end, or null.
        public WaterfallModel Fall { get; set; }

        public override float Level
        {
            get { return Path.ValuesAt(Path.Length).Position.y; }
        }

        public override bool Covers(Vector2 point)
        {
            var distance = Path.DistanceTo(point, out var along);
            return distance <= (Path.ValuesAt(along).Width * 0.5f) + Shape.BankReach;
        }
    }

    // Where a river starts or ends in a lake or a sea: the river lies at their level there.
    internal sealed class RiverJunction
    {
        public RiverJunction(OpenWaterModel body)
        {
            Body = body;
        }

        public OpenWaterModel Body { get; }
    }

    internal sealed class WaterfallModel
    {
        public WaterfallModel(WaterfallShape shape, RiverModel river, SurfaceModel landing)
        {
            Shape = shape;
            River = river;
            Landing = landing;
            var path = river.Path;
            var end = path.ValuesAt(path.Length);
            Fall = new WaterfallFall(
                path.PlanarAt(path.Length),
                path.DirectionAt(path.Length),
                end.Width * 0.5f,
                end.Position.y,
                end.Depth,
                landing.Level,
                shape.Speed);
        }

        public WaterfallShape Shape { get; }

        public RiverModel River { get; }

        public SurfaceModel Landing { get; }

        public WaterfallFall Fall { get; }
    }
}
