using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Steps every ClothChainSolver in play once a frame, from the camera that draws the bodies, so
    // a crowd costs about as much as a few bodies. A body the camera does not see moves nothing. Of
    // the bodies in view, the nearest SpringBodies within ClothChainSolver.NearDistance spring every
    // frame; the others within ClothChainSolver.FarDistance follow their targets in turns, the ones
    // that waited longest first, at most FollowSteps of them a frame and no more once their steps
    // took FollowMilliseconds, so a frame of costly cloaks over robes steps fewer; and the bodies
    // beyond rest. Between its turns a chain keeps its pose against the bone it hangs from. The
    // scheduler puts itself in play with the first solver. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    [DefaultExecutionOrder(PresentationExecutionOrder.Cloth)]
    public sealed class ClothChainScheduler : MonoBehaviour
    {
        public const int SpringBodies = 8;
        public const int FollowSteps = 16;
        public const double FollowMilliseconds = 1.5;

        // The camera sees a body while this cube around it, centred ViewCentreHeight above its feet,
        // lies in its view: wide enough for a cloak swinging in a jog and for a body lying dead.
        public const float ViewHalfSize = 2f;
        public const float ViewCentreHeight = 1f;

        private static readonly List<ClothChainSolver> Solvers = new List<ClothChainSolver>();
        private static readonly IComparer<Body> ByDistance = new DistanceOrder();
        private static readonly IComparer<Body> FollowersByTurn = new TurnOrder();
        private static ClothChainScheduler runner;

        private readonly Plane[] planes = new Plane[6];
        private readonly List<Body> bodies = new List<Body>();
        private readonly List<ClothChainSolver> stepping = new List<ClothChainSolver>();
        private readonly System.Diagnostics.Stopwatch followWatch = new System.Diagnostics.Stopwatch();

        // What becomes of one body's chains this frame: the detail they move with and whether they
        // step now. A follower that does not step keeps its pose until its turn.
        public struct Body
        {
            public Body(int id, float distance, bool inView, int steppedFrame)
            {
                Id = id;
                Distance = distance;
                InView = inView;
                SteppedFrame = steppedFrame;
                Detail = ClothChainSolver.Detail.Rest;
                Steps = false;
            }

            public int Id { get; }

            public float Distance { get; }

            public bool InView { get; }

            public int SteppedFrame { get; }

            public ClothChainSolver.Detail Detail { get; internal set; }

            public bool Steps { get; internal set; }
        }

        // Gives every body its detail for this frame and says which step, reordering the list.
        public static void Plan(List<Body> plan, int springBodies, int followSteps)
        {
            plan.Sort(ByDistance);
            var springs = 0;
            for (var index = 0; index < plan.Count; index++)
            {
                var body = plan[index];
                body.Steps = true;
                if (!body.InView)
                {
                    body.Detail = ClothChainSolver.Detail.Hidden;
                }
                else if (body.Distance > ClothChainSolver.FarDistance)
                {
                    body.Detail = ClothChainSolver.Detail.Rest;
                }
                else if (body.Distance <= ClothChainSolver.NearDistance && springs < springBodies)
                {
                    body.Detail = ClothChainSolver.Detail.Springs;
                    springs++;
                }
                else
                {
                    body.Detail = ClothChainSolver.Detail.Follow;
                    body.Steps = false;
                }

                plan[index] = body;
            }

            plan.Sort(FollowersByTurn);
            for (var index = 0; index < plan.Count && index < followSteps && plan[index].Detail == ClothChainSolver.Detail.Follow; index++)
            {
                var body = plan[index];
                body.Steps = true;
                plan[index] = body;
            }
        }

        internal static void Register(ClothChainSolver solver)
        {
            if (!Solvers.Contains(solver))
            {
                Solvers.Add(solver);
            }

            if (runner == null && Application.isPlaying)
            {
                var host = new GameObject(nameof(ClothChainScheduler));
                DontDestroyOnLoad(host);
                runner = host.AddComponent<ClothChainScheduler>();
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
            var camera = Camera.main;
            if (camera != null)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, planes);
            }

            var eye = camera != null ? camera.transform.position : Vector3.zero;
            bodies.Clear();
            stepping.Clear();
            foreach (var solver in Solvers)
            {
                if (solver == null || !solver.IsActive)
                {
                    continue;
                }

                var position = solver.transform.position;
                var inView = camera != null && GeometryUtility.TestPlanesAABB(planes, ViewBounds(solver.transform));
                bodies.Add(new Body(stepping.Count, Vector3.Distance(position, eye), inView, solver.SteppedFrame));
                stepping.Add(solver);
            }

            // The plan puts the followers first, in their turns.
            Plan(bodies, SpringBodies, FollowSteps);
            var deltaTime = Time.deltaTime;
            var frame = Time.frameCount;
            followWatch.Restart();
            foreach (var body in bodies)
            {
                if (!body.Steps
                    || (body.Detail == ClothChainSolver.Detail.Follow && followWatch.Elapsed.TotalMilliseconds >= FollowMilliseconds))
                {
                    continue;
                }

                var solver = stepping[body.Id];
                solver.Simulate(deltaTime, body.Detail);
                if (body.Detail == ClothChainSolver.Detail.Springs || body.Detail == ClothChainSolver.Detail.Follow)
                {
                    solver.SteppedFrame = frame;
                }
            }
        }

        private void OnDestroy()
        {
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

        private sealed class DistanceOrder : IComparer<Body>
        {
            public int Compare(Body x, Body y)
            {
                return x.Distance.CompareTo(y.Distance);
            }
        }

        // Followers first, the one that stepped longest ago first, then the nearer.
        private sealed class TurnOrder : IComparer<Body>
        {
            public int Compare(Body x, Body y)
            {
                var xFollows = x.Detail == ClothChainSolver.Detail.Follow;
                var yFollows = y.Detail == ClothChainSolver.Detail.Follow;
                if (xFollows != yFollows)
                {
                    return xFollows ? -1 : 1;
                }

                return x.SteppedFrame != y.SteppedFrame
                    ? x.SteppedFrame.CompareTo(y.SteppedFrame)
                    : x.Distance.CompareTo(y.Distance);
            }
        }
    }
}
