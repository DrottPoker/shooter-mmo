using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Moves the cloth chains of a body after its Animator posed it, for the garments that hang on
    // them: each chain springs toward hanging from its animated parent, keeps its bone lengths,
    // feels gravity, and is pushed out of capsules around the hips, torso, legs, and head, so a
    // robe follows the legs and swings with every step, in every clip, without keys of its own.
    // The capsules' radii are measured from the body's own mesh, so the solver fits every body type.
    // Near the camera the springs run, farther away the chains only follow their targets, and far
    // away they rest. It is presentation only and never reaches the server. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(PresentationExecutionOrder.Cloth)]
    public sealed class ClothChainSolver : MonoBehaviour
    {
        public const float NearDistance = 25f;
        public const float FarDistance = 60f;

        // A chain whose root jumps farther than this in one step, as in a teleport, starts again.
        public const float RestartDistance = 2f;

        public enum Detail
        {
            Springs,
            Follow,
            Rest,
        }

        private const float FrameSeconds = 1f / 60f;
        // Each particle alternates this often between leaving the capsules and its bone length.
        private const int ResolveIterations = 8;
        // Below this share of a capsule's push across the bone, a particle is pushed straight out.
        private const float MinimumSlope = 0.2f;
        // At most this many passes from the tip and back when a chain cannot leave the body otherwise.
        private const int ChainPasses = 3;
        private const float FallbackRadius = 0.05f;

        // The body bones the cloth collides with, each with the bone its capsule ends at. The head is
        // a sphere around its own vertices.
        private static readonly (string Bone, string End)[] CapsuleBones =
        {
            ("Hips", "Spine"), ("Spine", "Chest"), ("Chest", "UpperChest"), ("UpperChest", "Neck"), ("Neck", "Head"),
            ("Head", null), ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"),
            ("LowerLeg_R", "Foot_R"),
        };

        private readonly Dictionary<ClothChainKind, ClothProfile> profiles = new Dictionary<ClothChainKind, ClothProfile>();
        private readonly List<ChainState> chains = new List<ChainState>();
        private readonly List<Capsule> capsules = new List<Capsule>();
        private Dictionary<string, Transform> bonesByName;
        private bool capsulesMeasured;

        public Detail CurrentDetail { get; private set; } = Detail.Rest;

        public bool IsActive => chains.Count > 0;

        public IReadOnlyList<Capsule> Capsules => capsules;

        // Lets the chains of a kind carry cloth with the profile's behavior, such as a robe on the
        // skirt chains. A body without the chains, such as a model exported before they existed,
        // has nothing to move.
        public void Activate(ClothChainKind kind, ClothProfile profile)
        {
            Deactivate(kind);
            profiles[kind] = profile;
            var bones = BonesByName();
            foreach (var chain in ClothChains.All)
            {
                if (chain.Kind == kind && ChainState.TryCreate(chain, bones, out var state))
                {
                    chains.Add(state);
                }
            }

            if (!capsulesMeasured && chains.Count > 0)
            {
                MeasureCapsules(bones);
            }
        }

        // Puts the chains of a kind back to their rest pose and stops moving them.
        public void Deactivate(ClothChainKind kind)
        {
            profiles.Remove(kind);
            for (var index = chains.Count - 1; index >= 0; index--)
            {
                if (chains[index].Chain.Kind == kind)
                {
                    chains[index].Rest();
                    chains.RemoveAt(index);
                }
            }
        }

        // The particles of a chain, from its root to its tip, in world space; empty for a chain that
        // is not active.
        public IReadOnlyList<Vector3> ParticlesOf(ClothChain chain)
        {
            foreach (var state in chains)
            {
                if (state.Chain == chain)
                {
                    return state.Positions;
                }
            }

            return System.Array.Empty<Vector3>();
        }

        // How far the deepest particle of an active chain lies inside a capsule, in meters; zero
        // or less when every particle is outside.
        public float DeepestPenetration()
        {
            var deepest = float.NegativeInfinity;
            foreach (var state in chains)
            {
                for (var index = 1; index < state.Positions.Length; index++)
                {
                    foreach (var capsule in capsules)
                    {
                        deepest = Mathf.Max(deepest, capsule.Radius - capsule.DistanceTo(state.Positions[index]));
                    }
                }
            }

            return float.IsNegativeInfinity(deepest) ? 0f : deepest;
        }

        // One step of the solver, for the frame time and the camera the body is seen from.
        public void Simulate(float deltaTime, Vector3 cameraPosition)
        {
            if (chains.Count == 0)
            {
                CurrentDetail = Detail.Rest;
                return;
            }

            var distance = Vector3.Distance(transform.position, cameraPosition);
            CurrentDetail = distance <= NearDistance ? Detail.Springs : distance <= FarDistance ? Detail.Follow : Detail.Rest;
            foreach (var capsule in capsules)
            {
                capsule.Update();
            }

            var frames = Mathf.Max(deltaTime, 0f) / FrameSeconds;
            foreach (var state in chains)
            {
                state.Rest();
                if (CurrentDetail == Detail.Rest)
                {
                    state.Started = false;
                    continue;
                }

                var profile = profiles[state.Chain.Kind];
                state.Step(profile, CurrentDetail == Detail.Springs, frames, deltaTime);
                state.Resolve(capsules, profile.Margin);
                state.Apply();
            }
        }

        private void LateUpdate()
        {
            var camera = Camera.main;
            Simulate(Time.deltaTime, camera != null ? camera.transform.position : transform.position);
        }

        private void OnDisable()
        {
            foreach (var state in chains)
            {
                state.Rest();
                state.Started = false;
            }
        }

        private Dictionary<string, Transform> BonesByName()
        {
            if (bonesByName == null)
            {
                bonesByName = new Dictionary<string, Transform>();
                foreach (var child in GetComponentsInChildren<Transform>(true))
                {
                    bonesByName.TryAdd(child.name, child);
                }
            }

            return bonesByName;
        }

        // Every capsule's radius is the middle distance of the body vertices that its bone moves
        // most, from the bone's segment in the bind pose, so the cloth lies on this body's own shape.
        private void MeasureCapsules(Dictionary<string, Transform> bones)
        {
            capsulesMeasured = true;
            var distances = new Dictionary<Transform, List<float>>();
            var segments = new Dictionary<Transform, (Vector3 A, Vector3 B)>();
            foreach (var (boneName, endName) in CapsuleBones)
            {
                if (!bones.TryGetValue(boneName, out var bone))
                {
                    continue;
                }

                var end = endName != null && bones.TryGetValue(endName, out var endBone) ? endBone.localPosition : Vector3.zero;
                segments[bone] = (Vector3.zero, end);
                distances[bone] = new List<float>();
            }

            var heads = new Dictionary<Transform, List<Vector3>>();
            foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                CollectDistances(renderer, segments, distances, heads);
            }

            foreach (var (boneName, endName) in CapsuleBones)
            {
                if (!bones.TryGetValue(boneName, out var bone))
                {
                    continue;
                }

                var (a, b) = segments[bone];
                if (endName == null && heads.TryGetValue(bone, out var points) && points.Count > 0)
                {
                    // A sphere around the middle of the head's vertices.
                    var middle = Vector3.zero;
                    foreach (var point in points)
                    {
                        middle += point;
                    }

                    middle /= points.Count;
                    a = b = middle;
                    var list = distances[bone];
                    list.Clear();
                    foreach (var point in points)
                    {
                        list.Add(Vector3.Distance(point, middle));
                    }
                }

                capsules.Add(new Capsule(bone, a, b, Median(distances[bone])));
            }
        }

        private static void CollectDistances(SkinnedMeshRenderer renderer, Dictionary<Transform, (Vector3 A, Vector3 B)> segments,
            Dictionary<Transform, List<float>> distances, Dictionary<Transform, List<Vector3>> heads)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null || !mesh.isReadable)
            {
                return;
            }

            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var vertices = mesh.vertices;
            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var offset = 0;
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                var count = perVertex[vertex];
                var dominant = -1;
                var best = 0f;
                for (var index = 0; index < count; index++)
                {
                    var weight = weights[offset + index];
                    if (weight.weight > best)
                    {
                        best = weight.weight;
                        dominant = weight.boneIndex;
                    }
                }

                offset += count;
                if (dominant < 0 || dominant >= bones.Length || bones[dominant] == null)
                {
                    continue;
                }

                var bone = bones[dominant];
                if (!segments.TryGetValue(bone, out var segment))
                {
                    continue;
                }

                // The vertex in the bone's own space at the bind pose.
                var local = bindposes[dominant].MultiplyPoint3x4(vertices[vertex]);
                if (segment.A == segment.B)
                {
                    if (!heads.TryGetValue(bone, out var points))
                    {
                        heads[bone] = points = new List<Vector3>();
                    }

                    points.Add(local);
                }
                else
                {
                    distances[bone].Add(Capsule.SegmentDistance(local, segment.A, segment.B));
                }
            }
        }

        private static float Median(List<float> values)
        {
            if (values.Count == 0)
            {
                return FallbackRadius;
            }

            values.Sort();
            return values[values.Count / 2];
        }

        // A capsule around a body bone, in the bone's own space, placed in the world every step.
        public sealed class Capsule
        {
            private readonly Transform bone;
            private readonly Vector3 localA;
            private readonly Vector3 localB;
            private readonly float localRadius;

            public Capsule(Transform bone, Vector3 localA, Vector3 localB, float localRadius)
            {
                this.bone = bone;
                this.localA = localA;
                this.localB = localB;
                this.localRadius = localRadius;
                Update();
            }

            public string BoneName => bone.name;

            public Vector3 A { get; private set; }

            public Vector3 B { get; private set; }

            public float Radius { get; private set; }

            public void Update()
            {
                A = bone.TransformPoint(localA);
                B = bone.TransformPoint(localB);
                var scale = bone.lossyScale;
                Radius = localRadius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            }

            public float DistanceTo(Vector3 point)
            {
                return SegmentDistance(point, A, B);
            }

            // How far a point lies within the capsule and the margin, and the way out of it.
            public float Penetration(Vector3 point, float margin, out Vector3 normal)
            {
                var closest = Closest(point, A, B);
                var away = point - closest;
                var length = away.magnitude;
                // A point on the axis leaves the way the capsule's bone faces, never through it.
                normal = length > 1e-6f ? away / length : bone.forward;
                return Radius + margin - length;
            }

            public static float SegmentDistance(Vector3 point, Vector3 a, Vector3 b)
            {
                return Vector3.Distance(point, Closest(point, a, b));
            }

            private static Vector3 Closest(Vector3 point, Vector3 a, Vector3 b)
            {
                var axis = b - a;
                var lengthSquared = axis.sqrMagnitude;
                if (lengthSquared < 1e-12f)
                {
                    return a;
                }

                var t = Mathf.Clamp01(Vector3.Dot(point - a, axis) / lengthSquared);
                return a + axis * t;
            }
        }

        // One chain: its bones with their rest rotations, and one particle per joint and one at its
        // tip, which the springs and constraints move and the bones then point along.
        private sealed class ChainState
        {
            private readonly Transform[] bones;
            private readonly Quaternion[] restRotations;
            // From each bone to the next particle, in the bone's own space.
            private readonly Vector3[] childOffsets;
            private readonly float[] lengths;
            private readonly Vector3[] targets;
            private readonly Vector3[] previous;

            private ChainState(ClothChain chain, Transform[] bones)
            {
                Chain = chain;
                this.bones = bones;
                restRotations = new Quaternion[bones.Length];
                childOffsets = new Vector3[bones.Length];
                lengths = new float[bones.Length];
                for (var index = 0; index < bones.Length; index++)
                {
                    restRotations[index] = bones[index].localRotation;
                    // The chains hang straight in the rest pose, so the tip bone points like the one
                    // before it, as far.
                    childOffsets[index] = index + 1 < bones.Length
                        ? bones[index + 1].localPosition
                        : (index > 0 ? childOffsets[index - 1] : Vector3.down * 0.1f);
                    lengths[index] = Vector3.Scale(childOffsets[index], bones[index].lossyScale).magnitude;
                }

                Positions = new Vector3[bones.Length + 1];
                targets = new Vector3[bones.Length + 1];
                previous = new Vector3[bones.Length + 1];
            }

            public ClothChain Chain { get; }

            public Vector3[] Positions { get; }

            public bool Started { get; set; }

            public static bool TryCreate(ClothChain chain, Dictionary<string, Transform> bones, out ChainState state)
            {
                var found = new Transform[chain.Bones.Count];
                for (var index = 0; index < found.Length; index++)
                {
                    if (!bones.TryGetValue(chain.Bones[index], out found[index]))
                    {
                        state = null;
                        return false;
                    }
                }

                state = new ChainState(chain, found);
                return true;
            }

            public void Rest()
            {
                for (var index = 0; index < bones.Length; index++)
                {
                    bones[index].localRotation = restRotations[index];
                }
            }

            // Where every particle hangs from the animated parent with the bones at rest, and the
            // particles' move toward it: inertia, gravity, and the spring with springs on, straight
            // to it without them.
            public void Step(ClothProfile profile, bool springs, float frames, float deltaTime)
            {
                targets[0] = bones[0].position;
                for (var index = 0; index < bones.Length; index++)
                {
                    targets[index + 1] = bones[index].TransformPoint(childOffsets[index]);
                }

                var restart = !Started || Vector3.Distance(targets[0], Positions[0]) > RestartDistance;
                if (!springs || restart)
                {
                    for (var index = 0; index < Positions.Length; index++)
                    {
                        Positions[index] = targets[index];
                        previous[index] = targets[index];
                    }

                    Started = true;
                    return;
                }

                var keep = Mathf.Pow(1f - Mathf.Clamp01(profile.Damping), frames);
                var pull = 1f - Mathf.Pow(1f - Mathf.Clamp01(profile.Stiffness), frames);
                var fall = Physics.gravity * (profile.Gravity * deltaTime * deltaTime);
                Positions[0] = targets[0];
                previous[0] = targets[0];
                for (var index = 1; index < Positions.Length; index++)
                {
                    var current = Positions[index];
                    var next = current + (current - previous[index]) * keep + fall;
                    next += (targets[index] - next) * pull;
                    previous[index] = current;
                    Positions[index] = next;
                }
            }

            // Moves every particle out of the capsules at its bone's length from the one before, so
            // the bones that point at the particles end exactly where the particles are. A pass from
            // the root sets each particle in turn; when a particle cannot leave a capsule that way,
            // such as behind a thigh that swings back, a pass from the tip pulls the chain toward
            // where the tip is free, and the next pass from the root bends the chain around the leg.
            public void Resolve(List<Capsule> capsules, float margin)
            {
                FromRoot(capsules, margin);
                for (var pass = 0; pass < ChainPasses && Penetrates(capsules, margin); pass++)
                {
                    var last = Positions.Length - 1;
                    PushOut(last, capsules, margin);
                    for (var index = last - 1; index >= 1; index--)
                    {
                        var link = Positions[index] - Positions[index + 1];
                        var length = link.magnitude;
                        if (length > 1e-6f)
                        {
                            Positions[index] = Positions[index + 1] + link / length * lengths[index];
                        }

                        PushOut(index, capsules, margin);
                    }

                    FromRoot(capsules, margin);
                }
            }

            private void FromRoot(List<Capsule> capsules, float margin)
            {
                for (var index = 1; index < Positions.Length; index++)
                {
                    KeepLength(index);
                    for (var iteration = 0; iteration < ResolveIterations; iteration++)
                    {
                        var moved = false;
                        foreach (var capsule in capsules)
                        {
                            var depth = capsule.Penetration(Positions[index], margin, out var normal);
                            if (depth <= 0f)
                            {
                                continue;
                            }

                            // Slide around the joint before it, on the bone's reach, out of the capsule;
                            // straight out when the bone points into the capsule head on.
                            var link = (Positions[index] - Positions[index - 1]).normalized;
                            var tangent = normal - link * Vector3.Dot(normal, link);
                            var slope = tangent.magnitude;
                            Positions[index] += slope > MinimumSlope
                                ? tangent / slope * Mathf.Min(depth / slope, lengths[index - 1])
                                : normal * depth;
                            moved = true;
                        }

                        KeepLength(index);
                        if (!moved)
                        {
                            break;
                        }
                    }
                }
            }

            private void PushOut(int index, List<Capsule> capsules, float margin)
            {
                foreach (var capsule in capsules)
                {
                    var depth = capsule.Penetration(Positions[index], margin, out var normal);
                    if (depth > 0f)
                    {
                        Positions[index] += normal * depth;
                    }
                }
            }

            private bool Penetrates(List<Capsule> capsules, float margin)
            {
                for (var index = 1; index < Positions.Length; index++)
                {
                    foreach (var capsule in capsules)
                    {
                        // A little inside the margin is still outside the body.
                        if (capsule.Penetration(Positions[index], margin * 0.5f, out _) > 0f)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private void KeepLength(int index)
            {
                var link = Positions[index] - Positions[index - 1];
                var length = link.magnitude;
                Positions[index] = Positions[index - 1] + (length > 1e-6f
                    ? link / length * lengths[index - 1]
                    : targets[index] - targets[index - 1]);
            }

            // Turns every bone, from the root to the tip, to point at its next particle.
            public void Apply()
            {
                for (var index = 0; index < bones.Length; index++)
                {
                    var bone = bones[index];
                    var current = bone.rotation * childOffsets[index];
                    var wanted = Positions[index + 1] - bone.position;
                    if (current.sqrMagnitude > 1e-12f && wanted.sqrMagnitude > 1e-12f)
                    {
                        bone.rotation = Quaternion.FromToRotation(current, wanted) * bone.rotation;
                    }
                }
            }
        }
    }
}
