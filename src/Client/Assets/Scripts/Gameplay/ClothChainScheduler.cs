using System.Collections.Generic;
using Mmorpg.Client.Cloth;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Lays the cloth of every body in play once a frame, after the camera moved, from its view: one
    // ClothWorld holds every dressed body, and its Burst jobs lay every body the camera sees within
    // ClothChainSolver.FarDistance in its target pose. A body beyond rests, and a body the camera
    // does not see keeps its pose, so a crowd costs no more than the bodies in view. The scheduler
    // puts itself in play with the first solver and owns the world. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    [DefaultExecutionOrder(PresentationExecutionOrder.Cloth)]
    public sealed class ClothChainScheduler : MonoBehaviour
    {
        // The camera sees a body while this cube around it, centred ViewCentreHeight above its feet,
        // lies in its view: wide enough for a cloak swinging and for a body lying dead.
        public const float ViewHalfSize = 2f;
        public const float ViewCentreHeight = 1f;

        private static readonly List<ClothChainSolver> Solvers = new List<ClothChainSolver>();
        private static ClothChainScheduler runner;

        private readonly Plane[] planes = new Plane[6];
        private readonly List<Body> bodies = new List<Body>();
        private ClothWorld world;

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
        // and the detail it moves with.
        public struct Body
        {
            public Body(float distance, bool inView)
            {
                Distance = distance;
                InView = inView;
                Detail = ClothDetail.Rest;
            }

            public float Distance { get; }

            public bool InView { get; }

            public ClothDetail Detail { get; internal set; }
        }

        // Gives every body its detail for this frame: out of view it keeps its pose, beyond the far
        // distance it rests, and otherwise its chains are laid.
        public static void Plan(List<Body> plan)
        {
            for (var index = 0; index < plan.Count; index++)
            {
                var body = plan[index];
                body.Detail = !body.InView ? ClothDetail.Hidden : ClothChainSolver.DetailAt(body.Distance);
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
        }

        private void LateUpdate()
        {
            if (world == null)
            {
                return;
            }

            var camera = Camera.main;
            if (camera != null)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, planes);
            }

            var eye = camera != null ? camera.transform.position : Vector3.zero;
            bodies.Clear();
            foreach (var solver in Solvers)
            {
                var inView = solver != null && solver.IsActive && solver.World == world && camera != null
                    && GeometryUtility.TestPlanesAABB(planes, ViewBounds(solver.transform));
                bodies.Add(new Body(solver != null ? Vector3.Distance(solver.transform.position, eye) : 0f, inView));
            }

            Plan(bodies);
            for (var index = 0; index < Solvers.Count; index++)
            {
                var solver = Solvers[index];
                if (solver != null && solver.IsActive && solver.World == world)
                {
                    solver.ChooseDetail(bodies[index].Detail);
                }
            }

            world.Step();
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
