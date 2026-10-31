using System.Collections.Generic;
using Mmorpg.Client.Cloth;
using Unity.Profiling;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Lays the cloth of every body in play once a frame, after the camera moved, from its view: one
    // ClothWorld holds every dressed body, and its Burst jobs lay every body the camera sees within
    // ClothChainSolver.FarDistance in its target pose, and swing the cloth of the bodies largest on
    // the screen on it with springs, as many as the budget holds. A body beyond rests, and a body
    // the camera does not see keeps its pose, so a crowd costs no more than the bodies in view. The
    // scheduler puts itself in play with the first solver and owns the world. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    [DefaultExecutionOrder(PresentationExecutionOrder.Cloth)]
    public sealed class ClothChainScheduler : MonoBehaviour
    {
        // The camera sees a body while this cube around it, centred ViewCentreHeight above its feet,
        // lies in its view: wide enough for a cloak swinging and for a body lying dead.
        public const float ViewHalfSize = 2f;
        public const float ViewCentreHeight = 1f;

        // About this many bodies have springs, a robe and a cloak counting two.
        public const int SpringBudget = 48;

        // A body with springs keeps them against a body up to this much larger on the screen, so no
        // body flickers between the levels.
        public const float SpringHysteresis = 1.25f;

        // The height of a body of scale 1 that its size on the screen is measured by, in meters.
        public const float BodyHeight = 2f;

        private static readonly List<ClothChainSolver> Solvers = new List<ClothChainSolver>();
        private static readonly ProfilerMarker StepMarker = new ProfilerMarker("ClothChainScheduler.Step");
        private static ClothChainScheduler runner;

        private readonly Plane[] planes = new Plane[6];
        private readonly List<Body> bodies = new List<Body>();
        private readonly List<int> order = new List<int>();
        private ClothWorld world;

        // How many bodies' springs a frame gives, a robe and a cloak counting two: SpringBudget,
        // unless changed, as for a measurement.
        public static int Budget { get; set; } = SpringBudget;

        // The world of every body in play.
        internal static ClothWorld World
        {
            get
            {
                if (runner == null)
                {
                    var host = new GameObject(nameof(ClothChainScheduler));
                    DontDestroyOnLoad(host);
                    runner = host.AddComponent<ClothChainScheduler>();
                }

                return runner.world ??= new ClothWorld(ClothChainSolver.Layout);
            }
        }

        // A body in the plan of a frame: how far it is from the camera, whether the camera sees it,
        // its share of the screen's height, what its springs cost of the budget, whether it had them
        // last frame, and the detail it moves with.
        public struct Body
        {
            public Body(float distance, bool inView, float screenSize = 0f, int cost = 1, bool springing = false)
            {
                Distance = distance;
                InView = inView;
                ScreenSize = screenSize;
                Cost = cost;
                Springing = springing;
                Detail = ClothDetail.Rest;
            }

            public float Distance { get; }

            public bool InView { get; }

            public float ScreenSize { get; }

            public int Cost { get; }

            public bool Springing { get; }

            public ClothDetail Detail { get; internal set; }

            // How the body ranks for springs: by its size on the screen, larger while it has them.
            public float Rank => ScreenSize * (Springing ? SpringHysteresis : 1f);
        }

        // Gives every body its detail for this frame: out of view it keeps its pose, beyond the far
        // distance it rests, the largest on the screen have springs as far as the budget holds, and
        // every other body is laid in its target pose.
        public static void Plan(List<Body> plan, int budget = SpringBudget)
        {
            Plan(plan, budget, new List<int>());
        }

        // The same, ordering in a list it is given, so a frame allocates nothing.
        public static void Plan(List<Body> plan, int budget, List<int> order)
        {
            order.Clear();
            for (var index = 0; index < plan.Count; index++)
            {
                var body = plan[index];
                body.Detail = !body.InView ? ClothDetail.Hidden : ClothChainSolver.DetailAt(body.Distance);
                plan[index] = body;
                if (body.Detail == ClothDetail.Spring)
                {
                    // The largest first, and of equals the first in the plan.
                    var at = order.Count;
                    while (at > 0 && plan[order[at - 1]].Rank < body.Rank)
                    {
                        at--;
                    }

                    order.Insert(at, index);
                }
            }

            var used = 0;
            foreach (var index in order)
            {
                var body = plan[index];
                if (used + body.Cost <= budget)
                {
                    used += body.Cost;
                    continue;
                }

                body.Detail = ClothDetail.Pose;
                plan[index] = body;
            }
        }

        internal static void Register(ClothChainSolver solver)
        {
            if (!Solvers.Contains(solver))
            {
                Solvers.Add(solver);
            }
        }

        internal static void Unregister(ClothChainSolver solver)
        {
            Solvers.Remove(solver);
        }

        // Play without a domain reload keeps statics; the solvers of the last play are gone.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Solvers.Clear();
            runner = null;
            Budget = SpringBudget;
        }

        private void LateUpdate()
        {
            if (world == null)
            {
                return;
            }

            using (StepMarker.Auto())
            {
                Step();
            }
        }

        private void Step()
        {
            var camera = Camera.main;
            if (camera != null)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, planes);
            }

            var eye = camera != null ? camera.transform.position : Vector3.zero;
            bodies.Clear();
            foreach (var solver in Solvers)
            {
                var ours = solver != null && solver.IsActive && solver.World == world;
                var inView = ours && camera != null && GeometryUtility.TestPlanesAABB(planes, ViewBounds(solver.transform));
                var distance = solver != null ? Vector3.Distance(solver.transform.position, eye) : 0f;
                var size = ours && camera != null
                    ? BodyHeight * ClothChainSolver.LargestScale(solver.transform.lossyScale) / ViewHeight(camera, distance)
                    : 0f;
                bodies.Add(new Body(distance, inView, size, ours ? solver.SpringCost : 0,
                    ours && solver.CurrentDetail == ClothDetail.Spring));
            }

            Plan(bodies, Budget, order);
            for (var index = 0; index < Solvers.Count; index++)
            {
                var solver = Solvers[index];
                if (solver != null && solver.IsActive && solver.World == world)
                {
                    solver.ChooseDetail(bodies[index].Detail);
                }
            }

            world.Step(Time.deltaTime);
        }

        // The height of the camera's view at a distance from it, in meters.
        private static float ViewHeight(Camera camera, float distance)
        {
            return camera.orthographic
                ? 2f * camera.orthographicSize
                : 2f * Mathf.Max(distance, 0.1f) * Mathf.Tan(0.5f * camera.fieldOfView * Mathf.Deg2Rad);
        }

        private void OnDestroy()
        {
            world?.Dispose();
            world = null;
            if (runner == this)
            {
                runner = null;
            }
        }

        // The cube the camera must see for a body, scaled with it.
        public static Bounds ViewBounds(Transform body)
        {
            var scale = ClothChainSolver.LargestScale(body.lossyScale);
            return new Bounds(body.position + body.up * (ViewCentreHeight * scale), Vector3.one * (2f * ViewHalfSize * scale));
        }
    }
}
