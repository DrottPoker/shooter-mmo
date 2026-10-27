using System.Collections.Generic;
using Mmorpg.Client.Networking;
using Mmorpg.GameSimulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Gameplay
{
    // Draws the baked collision around the local player and the path it follows, while the Debug
    // menu asks for them. The collision lines are built again when the player moved a few meters
    // or the loaded chunks changed, and drawn every frame as one line mesh per kind; the path is
    // a few points and is built every frame.
    [DisallowMultipleComponent]
    public sealed class CollisionDebugOverlay : MonoBehaviour
    {
        // Half the side of the square of collision that shows around the player.
        public const float Reach = 24f;

        private const float RebuildDistance = 2f;
        private const float PathLift = 0.1f;

        private static readonly Color[] KindColors =
        {
            new Color(0.35f, 0.95f, 0.45f, 1f),
            new Color(1f, 0.82f, 0.25f, 1f),
            new Color(1f, 0.3f, 0.28f, 1f),
            new Color(1f, 0.55f, 0.12f, 1f),
            new Color(0.72f, 0.5f, 1f, 1f)
        };

        private static readonly Color PathColor = new Color(0.3f, 0.9f, 1f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private readonly CollisionDebugLines lines = new CollisionDebugLines();
        private readonly Mesh[] kindMeshes = new Mesh[CollisionDebugLines.KindCount];
        private readonly MaterialPropertyBlock[] kindColors = new MaterialPropertyBlock[CollisionDebugLines.KindCount];
        private readonly List<Vector3> pathPoints = new List<Vector3>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<int> indices = new List<int>();
        private RealtimeSimulationClient client;
        private Material material;
        private LocalPlayerController player;
        private Mesh pathMesh;
        private MaterialPropertyBlock pathColor;
        private ICollisionWorld builtWorld;
        private int builtChunkCount = -1;
        private Vector3 builtCenter;

        public bool ShowCollision { get; set; }

        public bool ShowPath { get; set; }

        // The shapes the collision lines show now.
        public int ShownShapeCount { get; private set; }

        // Called by its owner before the component is activated.
        public void Initialize(RealtimeSimulationClient realtimeClient, Material lineMaterial)
        {
            client = realtimeClient;
            material = lineMaterial;
        }

        // The color of a kind of collision, for the legend of the Debug menu.
        public static Color ColorOf(CollisionDebugKind kind)
        {
            return KindColors[(int)kind];
        }

        public static Color PathLineColor
        {
            get { return PathColor; }
        }

        private void Awake()
        {
            for (var kind = 0; kind < CollisionDebugLines.KindCount; kind++)
            {
                kindMeshes[kind] = CreateMesh("CollisionDebug" + (CollisionDebugKind)kind);
                kindColors[kind] = new MaterialPropertyBlock();
                kindColors[kind].SetColor(BaseColorId, KindColors[kind]);
            }

            pathMesh = CreateMesh("PathDebug");
            pathColor = new MaterialPropertyBlock();
            pathColor.SetColor(BaseColorId, PathColor);
        }

        private void OnDestroy()
        {
            foreach (var mesh in kindMeshes)
            {
                Destroy(mesh);
            }

            Destroy(pathMesh);
        }

        private void LateUpdate()
        {
            if (material == null || (!ShowCollision && !ShowPath))
            {
                return;
            }

            if (player == null)
            {
                player = FindAnyObjectByType<LocalPlayerController>();
            }

            var session = client != null ? client.MovementSession : null;
            if (player == null || !player.IsServerAuthoritative || session == null)
            {
                builtWorld = null;
                ShownShapeCount = 0;
                return;
            }

            if (ShowCollision)
            {
                RebuildCollisionIfNeeded(session);
                for (var kind = 0; kind < CollisionDebugLines.KindCount; kind++)
                {
                    Draw(kindMeshes[kind], kindColors[kind]);
                }
            }
            else
            {
                builtWorld = null;
            }

            if (ShowPath && BuildPath())
            {
                Draw(pathMesh, pathColor);
            }
        }

        private void RebuildCollisionIfNeeded(NetworkMovementSession session)
        {
            var world = session.CollisionWorld;
            var center = player.transform.position;
            if (ReferenceEquals(world, builtWorld)
                && world.ChunkCount == builtChunkCount
                && (center - builtCenter).sqrMagnitude < RebuildDistance * RebuildDistance)
            {
                return;
            }

            builtWorld = world;
            builtChunkCount = world.ChunkCount;
            builtCenter = center;
            lines.Build(world, center, Reach, session.Settings.CharacterCollision.MaximumSlopeDegrees);
            ShownShapeCount = lines.ShapeCount;
            for (var kind = 0; kind < CollisionDebugLines.KindCount; kind++)
            {
                SetLines(kindMeshes[kind], lines.Segments((CollisionDebugKind)kind));
            }
        }

        // From the player through every waypoint it has not reached yet.
        private bool BuildPath()
        {
            var path = player.CurrentPath;
            var progress = player.PredictedState.PathProgress;
            if (path == null || !progress.IsFollowing(path))
            {
                return false;
            }

            pathPoints.Clear();
            var previous = player.transform.position + Vector3.up * PathLift;
            for (var index = progress.NextWaypointIndex; index < path.Count; index++)
            {
                var waypoint = path[index];
                var next = new Vector3(waypoint.X, waypoint.Y + PathLift, waypoint.Z);
                pathPoints.Add(previous);
                pathPoints.Add(next);
                previous = next;
            }

            SetLines(pathMesh, pathPoints);
            return pathPoints.Count > 0;
        }

        private void SetLines(Mesh mesh, IReadOnlyList<Vector3> points)
        {
            mesh.Clear();
            if (points.Count == 0)
            {
                return;
            }

            vertices.Clear();
            indices.Clear();
            for (var index = 0; index < points.Count; index++)
            {
                vertices.Add(points[index]);
                indices.Add(index);
            }

            mesh.indexFormat = points.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
        }

        private void Draw(Mesh mesh, MaterialPropertyBlock color)
        {
            if (mesh.vertexCount == 0)
            {
                return;
            }

            var parameters = new RenderParams(material)
            {
                matProps = color,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer
            };
            Graphics.RenderMesh(parameters, mesh, 0, Matrix4x4.identity);
        }

        private static Mesh CreateMesh(string name)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            return mesh;
        }
    }
}
