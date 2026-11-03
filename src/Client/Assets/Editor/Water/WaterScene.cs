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

        // The shapes that make nothing until they are complete, with what each lacks: a lake or a
        // sea with fewer than three points, a river with fewer than two, and a waterfall without a
        // river or a landing that makes water, from a river another waterfall already falls from,
        // or onto a landing that does not lie below its lip.
        public Dictionary<WorldBuilderShape, string> Incomplete { get; } = new Dictionary<WorldBuilderShape, string>();

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
                    case LakeShape lake when lake.Shore.Count >= 3:
                        water.Lakes.Add(new LakeModel(lake));
                        break;
                    case LakeShape lake:
                        water.Incomplete.Add(lake, "A lake's shore needs at least three points.");
                        break;
                    case SeaShape sea when sea.Outline.Count >= 3:
                        water.Seas.Add(new SeaModel(sea));
                        break;
                    case SeaShape sea:
                        water.Incomplete.Add(sea, "A sea's outline needs at least three points.");
                        break;
                    case RiverShape river when river.Points.Count >= 2:
                        water.Rivers.Add(new RiverModel(river));
                        break;
                    case RiverShape river:
                        water.Incomplete.Add(river, "A river needs at least two points.");
                        break;
                    case WaterfallShape waterfall:
                        waterfalls.Add(waterfall);
                        break;
                }
            }

            // A river calms a sea that it starts or ends in.
            foreach (var river in water.Rivers)
            {
                river.Start = water.FindJunction(river.Path.ValuesAt(0f).Position);
                river.End = water.FindJunction(river.Path.ValuesAt(river.Path.Length).Position);
                foreach (var junction in new[] { river.Start, river.End })
                {
                    if (junction != null && junction.Body is SeaModel sea && !sea.Rivers.Contains(river))
                    {
                        sea.Rivers.Add(river);
                    }
                }
            }

            foreach (var waterfall in waterfalls)
            {
                var problem = water.WaterfallProblem(waterfall, out var river, out var landing, out var landingLevel);
                if (problem != null)
                {
                    water.Incomplete.Add(waterfall, problem);
                    continue;
                }

                var model = new WaterfallModel(waterfall, river, landing, landingLevel);
                river.Fall = model;
                landing.Landings.Add(model);
                water.Waterfalls.Add(model);
            }

            return water;
        }

        // What keeps a waterfall from falling, or null when it falls from its river's end into
        // the water of its landing, with the level of that water where the fall lands.
        private string WaterfallProblem(WaterfallShape waterfall, out RiverModel river, out SurfaceModel landing, out float landingLevel)
        {
            river = waterfall.River != null ? Rivers.Find(model => model.River == waterfall.River) : null;
            landing = FindSurface(waterfall.Landing);
            landingLevel = 0f;
            if (waterfall.River == null)
            {
                return "A waterfall falls from a river: choose its River.";
            }

            if (river == null)
            {
                return "Its River " + waterfall.River.name + " makes no water: its shape is disabled or inactive, "
                    + "or has fewer than two points.";
            }

            if (waterfall.Landing == null)
            {
                return "A waterfall falls into a water body: choose its Landing.";
            }

            if (landing == null)
            {
                return "Its Landing " + waterfall.Landing.name + " makes no water to fall into: it needs an active "
                    + "lake, river, or sea shape that is complete.";
            }

            if (landing == river)
            {
                return "A waterfall falls into another water body than the river it falls from.";
            }

            if (river.Fall != null)
            {
                return "Another waterfall already falls from " + waterfall.River.name + "; a river has one waterfall, at its end.";
            }

            landingLevel = LandingLevel(river, landing, waterfall.Speed);
            return river.Path.ValuesAt(river.Path.Length).Position.y - landingLevel < MinimumFall
                ? "The landing lies at or above the lip, so the water has nowhere to fall: lower the landing or raise the river's last point."
                : null;
        }

        // The level of the landing where the water from the river's end lands on it: a lake's or a
        // sea's level, or a river's surface where the fall reaches it, which in turn decides how
        // far out the water lands, found in a few steps.
        private static float LandingLevel(RiverModel river, SurfaceModel landing, float speed)
        {
            var path = river.Path;
            var surface = path.ValuesAt(path.Length).Position.y;
            var lip = path.PlanarAt(path.Length);
            var downstream = path.DirectionAt(path.Length);
            var level = landing.LevelAt(lip);
            for (var step = 0; step < 4; step++)
            {
                var seconds = Mathf.Sqrt(2f * Mathf.Max(0f, surface - level) / WaterfallFall.Gravity);
                level = landing.LevelAt(lip + (downstream * (speed * seconds)));
            }

            return level;
        }

        // The lake or sea whose surface covers a point at about its height, the highest if several do.
        public OpenWaterModel FindBodyAt(Vector3 point)
        {
            OpenWaterModel best = null;
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
            var body = FindBodyAt(point);
            return body != null ? new RiverJunction(body) : null;
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

        // The height of the drawn surface at the point in X and Z.
        public abstract float LevelAt(Vector2 point);

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

        // The height of the whole surface.
        public float Level
        {
            get { return Shape.transform.position.y; }
        }

        public override float LevelAt(Vector2 point)
        {
            return Level;
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

        // The rivers that start or end in the sea, whose water calms it.
        public List<RiverModel> Rivers { get; } = new List<RiverModel>();
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

        // The height of the surface's rows where the river passes nearest the point, still and at
        // the level of the water it starts or ends in, once its junctions are known.
        public override float LevelAt(Vector2 point)
        {
            Path.DistanceTo(point, out var along);
            var rows = RiverSurfaceBuilder.Rows(this);
            for (var index = 1; index < rows.Count; index++)
            {
                if (rows[index].Along >= along)
                {
                    var from = rows[index - 1];
                    var to = rows[index];
                    var span = to.Along - from.Along;
                    return span > 0f ? Mathf.Lerp(from.Height, to.Height, (along - from.Along) / span) : to.Height;
                }
            }

            return rows[rows.Count - 1].Height;
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
        // The landing level is the height of the landing's surface where the water lands on it.
        public WaterfallModel(WaterfallShape shape, RiverModel river, SurfaceModel landing, float landingLevel)
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
                landingLevel,
                shape.Speed);
        }

        public WaterfallShape Shape { get; }

        public RiverModel River { get; }

        public SurfaceModel Landing { get; }

        public WaterfallFall Fall { get; }
    }
}
