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
    // enabled views besides the main camera, such as the Equipment window's character preview,
    // count too: a body is measured from the camera it looks largest in. The scheduler puts itself
    // in play with the first solver and owns the world. See docs/EQUIPMENT_VISUALS_DESIGN.md.
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
        private static readonly List<Camera> Views = new List<Camera>();
        private static readonly ProfilerMarker StepMarker = new ProfilerMarker("ClothChainScheduler.Step");
        private static ClothChainScheduler runner;

        // The cameras of this frame, the main camera first, and the planes of each one's view.
        private readonly List<Camera> cameras = new List<Camera>();
        private readonly List<Plane[]> cameraPlanes = new List<Plane[]>();
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

        // A camera besides the main camera whose view counts while it is enabled.
        public static void AddView(Camera camera)
        {
            if (camera != null && !Views.Contains(camera))
            {
                Views.Add(camera);
            }
        }

        public static void RemoveView(Camera camera)
        {
            Views.Remove(camera);
        }

        // Play without a domain reload keeps statics; the solvers and views of the last play are
        // gone.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Solvers.Clear();
            Views.Clear();
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
            var main = Camera.main;
            CollectCameras(main);
            var eye = main != null ? main.transform.position : Vector3.zero;
            bodies.Clear();
            foreach (var solver in Solvers)
            {
                var ours = solver != null && solver.IsActive && solver.World == world;
                var inView = false;
                var size = 0f;
                var distance = solver != null ? Vector3.Distance(solver.transform.position, eye) : 0f;
                if (ours)
                {
                    Measure(solver.transform, eye, cameras, cameraPlanes, out inView, out distance, out size);
                }

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

        // Whether any of the cameras sees the body, and its distance from and share of the view
        // height of the camera it looks largest in; a body no camera sees is measured from the eye.
        public static void Measure(
            Transform body,
            Vector3 eye,
            IReadOnlyList<Camera> cameras,
            IReadOnlyList<Plane[]> planes,
            out bool inView,
            out float distance,
            out float screenSize)
        {
            inView = false;
            screenSize = 0f;
            distance = Vector3.Distance(body.position, eye);
            var bounds = ViewBounds(body);
            var scale = ClothChainSolver.LargestScale(body.lossyScale);
            for (var index = 0; index < cameras.Count; index++)
            {
                if (!GeometryUtility.TestPlanesAABB(planes[index], bounds))
                {
                    continue;
                }

                var camera = cameras[index];
                var cameraDistance = Vector3.Distance(body.position, camera.transform.position);
                var size = BodyHeight * scale / ViewHeight(camera, cameraDistance);
                if (!inView || size > screenSize)
                {
                    inView = true;
                    screenSize = size;
                    distance = cameraDistance;
                }
            }
        }

        // The main camera and the enabled views, each with the planes of its view, reusing the
        // plane arrays, so a frame allocates nothing.
        private void CollectCameras(Camera main)
        {
            cameras.Clear();
            if (main != null)
            {
                cameras.Add(main);
            }

            for (var index = Views.Count - 1; index >= 0; index--)
            {
                var view = Views[index];
                if (view == null)
                {
                    Views.RemoveAt(index);
                }
                else if (view != main && view.isActiveAndEnabled)
                {
                    cameras.Add(view);
                }
            }

            while (cameraPlanes.Count < cameras.Count)
            {
                cameraPlanes.Add(new Plane[6]);
            }

            for (var index = 0; index < cameras.Count; index++)
            {
                GeometryUtility.CalculateFrustumPlanes(cameras[index], cameraPlanes[index]);
            }
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
