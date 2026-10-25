using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // Measures the loose cloth of a dressed body in motion, frame by frame, as ClothMeasurement and
    // the Wardrobe read it:
    // - how many of the body's leg and arm vertices come through the loose cloth, and how far, and
    //   how many of an inner garment's loose cloth come through the loose cloth over it, as a robe's
    //   skirt through a cloak: a vertex comes through when the cloth crosses the way to it from the
    //   bone it hangs on, or for an inner garment from the hips' upright axis;
    // - how much the chains jerk in the frame of the bone they hang from, in steady motion;
    // - how far a spin flares the chains out, how long they take to settle after a stop or a spin,
    //   whether the skirt's chains keep their order around the hips, and how far a jump moves them.
    // See docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothMotionMeter : IDisposable
    {
        // Cloth this far or less from a vertex past it still covers it, in meters.
        public const float PokeTolerance = 0.002f;

        // A chain moving slower than this in the frame of its bone has settled, in meters a second.
        public const float SettledSpeed = 0.05f;

        private const float GridCell = 0.05f;

        private readonly WardrobePreview preview;
        private readonly Dictionary<string, Transform> bones;
        private readonly List<Chain> chains = new List<Chain>();
        private readonly List<Mesh> owned = new List<Mesh>();
        private readonly List<GameObject> measured = new List<GameObject>();
        private readonly List<Skin> limbs = new List<Skin>();
        private readonly List<Cloth> cloths = new List<Cloth>();
        private string wornKey;
        private float[] restTipRadius = new float[0];
        private int samples;
        private float time;
        private float sumSquaredJerk;
        private int jerkCount;
        private float lastUnsettled;

        public ClothMotionMeter(WardrobePreview preview)
        {
            this.preview = preview;
            bones = preview.Body.GetComponentsInChildren<Transform>(true).GroupBy(bone => bone.name)
                .ToDictionary(group => group.Key, group => group.First());
        }

        // What was measured since the meter started.
        public ClothMotionReport Report { get; private set; } = new ClothMotionReport();

        // How many vertices and how far came through the loose cloth in the last frame that measured them.
        public int LastPoked { get; private set; }

        public float LastPokeDepth { get; private set; }

        public void Dispose()
        {
            Release();
        }

        // Measures the frame the preview just stepped: the motion's time, its frame time, and whether
        // to measure the vertices that come through the cloth, which takes milliseconds.
        public void Sample(ClothStressMotion motion, float motionTime, float deltaTime, bool meshes)
        {
            Rebuild();
            var report = Report;
            report.Frames++;
            var steady = motion != null && motion.Steady.Any(phase => motionTime >= phase.Start && motionTime <= phase.End);
            var jumped = motion != null && motion.Jumps.Any(jump => jump > time && jump <= motionTime);
            var spinning = motion != null && motion.Spin.HasValue && motionTime >= motion.Spin.Value.Start
                && motionTime <= motion.Spin.Value.End;
            var settling = motion != null && motion.Settle.HasValue && motionTime >= motion.Settle.Value.Start
                && motionTime <= motion.Settle.Value.End;
            time = motionTime;
            var hips = bones["Hips"];
            var up = preview.Body.transform.up;
            foreach (var chain in chains)
            {
                var particles = preview.Solver.ParticlesOf(chain.Definition);
                if (particles.Count == 0)
                {
                    continue;
                }

                chain.Push(particles, chain.Anchor, deltaTime);
                if (chain.HasJerk && steady)
                {
                    foreach (var jerk in chain.Jerks)
                    {
                        sumSquaredJerk += jerk * jerk;
                        jerkCount++;
                        report.JerkMax = Mathf.Max(report.JerkMax, jerk);
                    }
                }

                if (jumped && chain.HasSpeed)
                {
                    report.JumpMove = Mathf.Max(report.JumpMove, chain.LastMove);
                }

                if (settling && chain.HasSpeed && chain.Speed > SettledSpeed)
                {
                    lastUnsettled = motionTime;
                }
            }

            var skirt = chains.Where(chain => chain.Definition.Kind == ClothChainKind.Skirt && preview.Solver.ParticlesOf(chain.Definition).Count > 0)
                .OrderBy(chain => chain.Definition.Angle).ToArray();
            var tips = chains.Where(chain => preview.Solver.ParticlesOf(chain.Definition).Count > 0).ToArray();
            if (samples == 0 || restTipRadius.Length != tips.Length)
            {
                restTipRadius = tips.Select(chain => Radius(hips.position, up, Tip(chain))).ToArray();
            }

            if (spinning)
            {
                for (var index = 0; index < tips.Length; index++)
                {
                    report.Flare = Mathf.Max(report.Flare, Radius(hips.position, up, Tip(tips[index])) - restTipRadius[index]);
                }
            }

            if (skirt.Length > 2 && !InOrder(skirt, hips.position, up))
            {
                report.OrderBreaks++;
            }

            if (meshes)
            {
                MeasureMeshes(report, hips.position, up);
            }

            report.JerkRms = jerkCount > 0 ? Mathf.Sqrt(sumSquaredJerk / jerkCount) : 0f;
            report.SettleSeconds = motion != null && motion.Settle.HasValue ? Mathf.Max(0f, lastUnsettled - motion.Settle.Value.Start) : 0f;
            samples++;
        }

        // Starts measuring again, as for another motion.
        public void Reset()
        {
            Report = new ClothMotionReport();
            foreach (var chain in chains)
            {
                chain.Clear();
            }

            samples = 0;
            time = 0f;
            sumSquaredJerk = 0f;
            jerkCount = 0;
            lastUnsettled = 0f;
        }

        private static float Radius(Vector3 centre, Vector3 up, Vector3 point)
        {
            var offset = point - centre;
            return (offset - up * Vector3.Dot(offset, up)).magnitude;
        }

        private Vector3 Tip(Chain chain)
        {
            var particles = preview.Solver.ParticlesOf(chain.Definition);
            return particles[particles.Count - 1];
        }

        // Whether the skirt's tips lie around the hips in the order of their chains, as the body faces.
        private bool InOrder(Chain[] skirt, Vector3 hips, Vector3 up)
        {
            var forward = Vector3.ProjectOnPlane(preview.Body.transform.forward, up).normalized;
            var right = Vector3.Cross(up, forward);
            var angles = skirt.Select(chain =>
            {
                var offset = Vector3.ProjectOnPlane(Tip(chain) - hips, up);
                return Mathf.Atan2(Vector3.Dot(offset, right), Vector3.Dot(offset, forward));
            }).ToArray();
            var turns = 0;
            for (var index = 0; index < angles.Length; index++)
            {
                if (Mathf.DeltaAngle(angles[index] * Mathf.Rad2Deg, angles[(index + 1) % angles.Length] * Mathf.Rad2Deg) < 0f)
                {
                    turns++;
                }
            }

            // Around the hips once, every step to the next chain turns the same way.
            return turns == 0;
        }

        private void MeasureMeshes(ClothMotionReport report, Vector3 hips, Vector3 up)
        {
            var poked = 0;
            var pokeDepth = 0f;
            foreach (var limb in limbs)
            {
                limb.Bake();
            }

            foreach (var cloth in cloths)
            {
                cloth.Bake();
            }

            foreach (var cloth in cloths)
            {
                foreach (var limb in limbs)
                {
                    for (var index = 0; index < limb.Vertices.Count; index++)
                    {
                        var vertex = limb.Vertices[index];
                        var depth = cloth.DepthPast(limb.Core(index), vertex);
                        if (depth > PokeTolerance)
                        {
                            poked++;
                            pokeDepth = Mathf.Max(pokeDepth, depth);
                        }
                    }
                }
            }

            // An inner garment's loose cloth under the loose cloth of a higher layer.
            var layerPoked = 0;
            for (var outer = 0; outer < cloths.Count; outer++)
            {
                for (var inner = 0; inner < cloths.Count; inner++)
                {
                    if (cloths[inner].Layer >= cloths[outer].Layer)
                    {
                        continue;
                    }

                    foreach (var vertex in cloths[inner].LooseVertices())
                    {
                        var core = hips + up * Vector3.Dot(vertex - hips, up);
                        var depth = cloths[outer].DepthPast(core, vertex);
                        if (depth > PokeTolerance)
                        {
                            layerPoked++;
                            report.LayerPokeDepth = Mathf.Max(report.LayerPokeDepth, depth);
                        }
                    }
                }
            }

            LastPoked = poked;
            LastPokeDepth = pokeDepth;
            report.Poked = Mathf.Max(report.Poked, poked);
            report.PokeDepth = Mathf.Max(report.PokeDepth, pokeDepth);
            report.LayerPoked = Mathf.Max(report.LayerPoked, layerPoked);
        }

        // Builds the chains and the measuring renderers again when the body wears other garments.
        private void Rebuild()
        {
            var key = string.Join(",", preview.Worn.Select(garment => garment.Name));
            if (key == wornKey)
            {
                return;
            }

            Release();
            wornKey = key;
            foreach (var definition in ClothChains.All)
            {
                if (bones.TryGetValue(definition.Parent, out var anchor))
                {
                    chains.Add(new Chain(definition, anchor));
                }
            }

            foreach (var part in preview.Body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (part.sharedMesh != null && part.name != PlayerBodyAppearance.CombinedRendererName && part.gameObject.hideFlags == HideFlags.None)
                {
                    var skin = Skin.Of(Copy(part.sharedMesh, part.bones, part.rootBone), bones);
                    if (skin != null)
                    {
                        limbs.Add(skin);
                    }
                }
            }

            foreach (var garment in PlayerBodyMeshes.Drawn(preview.Worn))
            {
                if (PlayerBodyAppearance.ChainsOf(garment.Cloth) == null || garment.Model == null)
                {
                    continue;
                }

                foreach (var renderer in garment.Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mapped = renderer.bones.Select(bone => bone != null && bones.TryGetValue(bone.name, out var own) ? own : null).ToArray();
                    var root = renderer.rootBone != null && bones.TryGetValue(renderer.rootBone.name, out var ownRoot) ? ownRoot : bones["Hips"];
                    cloths.Add(new Cloth(Copy(renderer.sharedMesh, mapped, root), garment.Layer, owned));
                }
            }

            Reset();
        }

        // A hidden renderer of a mesh on the body's own bones, for baking.
        private SkinnedMeshRenderer Copy(Mesh mesh, Transform[] meshBones, Transform root)
        {
            var copy = new GameObject("ClothMeter") { hideFlags = HideFlags.HideAndDontSave };
            copy.transform.SetParent(preview.Body.transform, false);
            measured.Add(copy);
            var renderer = copy.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = meshBones;
            renderer.rootBone = root;
            renderer.forceRenderingOff = true;
            renderer.updateWhenOffscreen = true;
            return renderer;
        }

        private void Release()
        {
            foreach (var copy in measured)
            {
                if (copy != null)
                {
                    Object.DestroyImmediate(copy);
                }
            }

            foreach (var mesh in owned)
            {
                Object.DestroyImmediate(mesh);
            }

            foreach (var limb in limbs)
            {
                limb.Dispose();
            }

            measured.Clear();
            owned.Clear();
            limbs.Clear();
            cloths.Clear();
            chains.Clear();
            wornKey = null;
        }

        // How fast a point's acceleration changes over its last four places, the newest first, from
        // the frame times between them, the newest first.
        public static float Jerk(Vector3 x0, Vector3 x1, Vector3 x2, Vector3 x3, float h0, float h1, float h2)
        {
            var v1 = (x0 - x1) / h0;
            var v2 = (x1 - x2) / h1;
            var v3 = (x2 - x3) / h2;
            var a1 = (v1 - v2) / (0.5f * (h0 + h1));
            var a2 = (v2 - v3) / (0.5f * (h1 + h2));
            return (a1 - a2).magnitude / (0.25f * h0 + 0.5f * h1 + 0.25f * h2);
        }

        // The bone each vertex of a mesh weighs most, or -1.
        private static int[] DominantBones(Mesh mesh)
        {
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var found = new int[perVertex.Length];
            var offset = 0;
            for (var vertex = 0; vertex < perVertex.Length; vertex++)
            {
                var dominant = -1;
                var best = 0f;
                for (var index = 0; index < perVertex[vertex]; index++)
                {
                    var weight = weights[offset + index];
                    if (weight.weight > best)
                    {
                        best = weight.weight;
                        dominant = weight.boneIndex;
                    }
                }

                offset += perVertex[vertex];
                found[vertex] = dominant;
            }

            return found;
        }

        // A chain's particles in the frame of the bone it hangs from, the last four frames of them.
        private sealed class Chain
        {
            private readonly List<Vector3[]> history = new List<Vector3[]>();
            private readonly List<float> steps = new List<float>();

            public Chain(ClothChain definition, Transform anchor)
            {
                Definition = definition;
                Anchor = anchor;
            }

            public ClothChain Definition { get; }

            public Transform Anchor { get; }

            public bool HasSpeed => history.Count >= 2;

            public bool HasJerk => history.Count >= 4;

            // The fastest particle's speed in the anchor's frame over the last frame.
            public float Speed { get; private set; }

            // How far the particle that moved most moved in the anchor's frame over the last frame.
            public float LastMove { get; private set; }

            // Every particle's jerk over the last four frames, from their own frame times: the
            // velocities over the three frames, the accelerations between their middles, and the
            // change between the accelerations' times, which for equal frames is the third
            // difference over the frame time cubed.
            public IEnumerable<float> Jerks
            {
                get
                {
                    var h0 = steps[steps.Count - 1];
                    var h1 = steps[steps.Count - 2];
                    var h2 = steps[steps.Count - 3];
                    var x0 = history[history.Count - 1];
                    var x1 = history[history.Count - 2];
                    var x2 = history[history.Count - 3];
                    var x3 = history[history.Count - 4];
                    for (var index = 1; index < x0.Length; index++)
                    {
                        yield return Jerk(x0[index], x1[index], x2[index], x3[index], h0, h1, h2);
                    }
                }
            }

            public void Push(IReadOnlyList<Vector3> particles, Transform anchor, float deltaTime)
            {
                var local = new Vector3[particles.Count];
                for (var index = 0; index < local.Length; index++)
                {
                    local[index] = anchor.InverseTransformPoint(particles[index]);
                }

                history.Add(local);
                steps.Add(deltaTime);
                if (history.Count > 4)
                {
                    history.RemoveAt(0);
                    steps.RemoveAt(0);
                }

                if (HasSpeed)
                {
                    var before = history[history.Count - 2];
                    LastMove = 0f;
                    for (var index = 1; index < local.Length && index < before.Length; index++)
                    {
                        LastMove = Mathf.Max(LastMove, Vector3.Distance(local[index], before[index]));
                    }

                    Speed = deltaTime > 0f ? LastMove / deltaTime : 0f;
                }
            }

            public void Clear()
            {
                history.Clear();
                steps.Clear();
            }
        }

        // A body part's leg and arm vertices, each with the segment of the bone it weighs most.
        private sealed class Skin : IDisposable
        {
            private readonly SkinnedMeshRenderer renderer;
            private readonly int[] chosen;
            private readonly Transform[] from;
            private readonly Transform[] to;
            private readonly Mesh baked = new Mesh();
            private readonly List<Vector3> bakedVertices = new List<Vector3>();
            private readonly List<Vector3> vertices = new List<Vector3>();

            private Skin(SkinnedMeshRenderer renderer, int[] chosen, Transform[] from, Transform[] to)
            {
                this.renderer = renderer;
                this.chosen = chosen;
                this.from = from;
                this.to = to;
            }

            public IReadOnlyList<Vector3> Vertices => vertices;

            public static Skin Of(SkinnedMeshRenderer renderer, Dictionary<string, Transform> bones)
            {
                var dominant = DominantBones(renderer.sharedMesh);
                var chosen = new List<int>();
                var from = new List<Transform>();
                var to = new List<Transform>();
                for (var vertex = 0; vertex < dominant.Length; vertex++)
                {
                    var bone = dominant[vertex] >= 0 ? renderer.bones[dominant[vertex]] : null;
                    var end = bone != null ? EndOf(bone.name) : null;
                    if (end != null && bones.TryGetValue(end, out var child))
                    {
                        chosen.Add(vertex);
                        from.Add(bones[bone.name]);
                        to.Add(child);
                    }
                }

                return chosen.Count > 0 ? new Skin(renderer, chosen.ToArray(), from.ToArray(), to.ToArray()) : null;
            }

            // The bone a leg or arm bone's segment ends at, or null for any other bone.
            private static string EndOf(string bone)
            {
                foreach (var (part, end) in new[]
                         {
                             ("UpperLeg", "LowerLeg"), ("LowerLeg", "Foot"), ("Foot", "Toes"), ("UpperArm", "LowerArm"),
                             ("LowerArm", "Hand"),
                         })
                {
                    if (bone.StartsWith(part, StringComparison.Ordinal))
                    {
                        return end + bone.Substring(part.Length);
                    }
                }

                return null;
            }

            public void Bake()
            {
                renderer.BakeMesh(baked);
                baked.GetVertices(bakedVertices);
                vertices.Clear();
                var toWorld = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                foreach (var vertex in chosen)
                {
                    vertices.Add(toWorld.MultiplyPoint3x4(bakedVertices[vertex]));
                }
            }

            // The point of the bone's segment nearest the vertex.
            public Vector3 Core(int index)
            {
                return ClothChainSolver.Capsule.Closest(vertices[index], from[index].position, to[index].position);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(baked);
            }
        }

        // A garment's loose cloth as posed: the triangles a chain bone moves, in a grid for the rays.
        private sealed class Cloth
        {
            private readonly SkinnedMeshRenderer renderer;
            private readonly Mesh baked;
            private readonly int[] triangles;
            private readonly bool[] loose;
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

            public Cloth(SkinnedMeshRenderer renderer, int layer, List<Mesh> owned)
            {
                this.renderer = renderer;
                Layer = layer;
                baked = new Mesh();
                owned.Add(baked);
                var mesh = renderer.sharedMesh;
                var dominant = DominantBones(mesh);
                loose = dominant.Select(bone => bone >= 0 && renderer.bones[bone] != null && IsChainBone(renderer.bones[bone].name)).ToArray();
                var kept = new List<int>();
                var all = mesh.triangles;
                for (var index = 0; index < all.Length; index += 3)
                {
                    if (loose[all[index]] || loose[all[index + 1]] || loose[all[index + 2]])
                    {
                        kept.Add(all[index]);
                        kept.Add(all[index + 1]);
                        kept.Add(all[index + 2]);
                    }
                }

                triangles = kept.ToArray();
            }

            public int Layer { get; }

            private static bool IsChainBone(string bone)
            {
                return ClothChains.All.Any(chain => chain.Bones.Contains(bone));
            }

            public void Bake()
            {
                renderer.BakeMesh(baked);
                baked.GetVertices(vertices);
                var toWorld = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, Vector3.one);
                for (var index = 0; index < vertices.Count; index++)
                {
                    vertices[index] = toWorld.MultiplyPoint3x4(vertices[index]);
                }

                grid.Clear();
                for (var index = 0; index < triangles.Length; index += 3)
                {
                    var a = vertices[triangles[index]];
                    var b = vertices[triangles[index + 1]];
                    var c = vertices[triangles[index + 2]];
                    var low = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                    var high = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                    for (var x = low.x; x <= high.x; x++)
                    {
                        for (var y = low.y; y <= high.y; y++)
                        {
                            for (var z = low.z; z <= high.z; z++)
                            {
                                var key = new Vector3Int(x, y, z);
                                if (!grid.TryGetValue(key, out var list))
                                {
                                    list = new List<int>();
                                    grid[key] = list;
                                }

                                list.Add(index);
                            }
                        }
                    }
                }
            }

            public IEnumerable<Vector3> LooseVertices()
            {
                for (var index = 0; index < vertices.Count; index++)
                {
                    if (loose[index])
                    {
                        yield return vertices[index];
                    }
                }
            }

            // How far past the cloth a point lies, seen from a core inside it: the distance from
            // where the way from the core to the point last crosses the cloth to the point, or zero.
            public float DepthPast(Vector3 core, Vector3 point)
            {
                var way = point - core;
                var length = way.magnitude;
                if (length < 1e-6f)
                {
                    return 0f;
                }

                var low = Cell(Vector3.Min(core, point));
                var high = Cell(Vector3.Max(core, point));
                var last = -1f;
                for (var x = low.x; x <= high.x; x++)
                {
                    for (var y = low.y; y <= high.y; y++)
                    {
                        for (var z = low.z; z <= high.z; z++)
                        {
                            if (!grid.TryGetValue(new Vector3Int(x, y, z), out var list))
                            {
                                continue;
                            }

                            foreach (var index in list)
                            {
                                if (Crosses(core, way, vertices[triangles[index]], vertices[triangles[index + 1]], vertices[triangles[index + 2]],
                                        out var share))
                                {
                                    last = Mathf.Max(last, share);
                                }
                            }
                        }
                    }
                }

                return last >= 0f ? (1f - last) * length : 0f;
            }

            private static Vector3Int Cell(Vector3 point)
            {
                return new Vector3Int(Mathf.FloorToInt(point.x / GridCell), Mathf.FloorToInt(point.y / GridCell), Mathf.FloorToInt(point.z / GridCell));
            }

            // Whether the segment from the origin along the way crosses the triangle, and where along it.
            private static bool Crosses(Vector3 origin, Vector3 way, Vector3 a, Vector3 b, Vector3 c, out float share)
            {
                share = 0f;
                var edge1 = b - a;
                var edge2 = c - a;
                var p = Vector3.Cross(way, edge2);
                var determinant = Vector3.Dot(edge1, p);
                if (Mathf.Abs(determinant) < 1e-12f)
                {
                    return false;
                }

                var inverse = 1f / determinant;
                var t = origin - a;
                var u = Vector3.Dot(t, p) * inverse;
                if (u < 0f || u > 1f)
                {
                    return false;
                }

                var q = Vector3.Cross(t, edge1);
                var v = Vector3.Dot(way, q) * inverse;
                if (v < 0f || u + v > 1f)
                {
                    return false;
                }

                share = Vector3.Dot(edge2, q) * inverse;
                return share > 0f && share < 1f;
            }
        }
    }

    // What ClothMotionMeter measured of a body's cloth in a motion. Lengths in meters, jerk in
    // meters per second cubed.
    public sealed class ClothMotionReport
    {
        public int Frames;
        public int Poked;
        public float PokeDepth;
        public int LayerPoked;
        public float LayerPokeDepth;
        public float JerkRms;
        public float JerkMax;
        public float Flare;
        public float SettleSeconds;
        public int OrderBreaks;
        public float JumpMove;
    }
}
