using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Moves the cloth chains of a body after its Animator posed it, for the garments that hang on
    // them: each chain springs toward hanging from its animated parent, keeps its bone lengths,
    // feels gravity, and is pushed out of capsules around the hips, torso, legs, and head. Skirt and
    // cloak chains also follow the legs: each bone beside a thigh or a shin turns as that part of the
    // leg turns against the hips, but only as far as the leg pushes the cloth outward, the way the
    // chain faces from the hips; the front chains follow the leg in front, the back chains and the
    // cloak the leg behind, and the side chains the leg on their side, blended by their angle around
    // the hips. So a robe drapes over bent legs, a cloak is pushed back by a leg that kicks back,
    // and both hang down over a leg that moves away from them, in every clip, without keys of their
    // own. A cloak worn over a robe lies over its skirt: the skirt chains are solved first, and the
    // cloak's particles are kept outside the skirt's cloth, its envelope, by how far the cloak's own
    // cloth lies within its chains and a gap. How far each garment's cloth reaches beyond and within
    // its chains is measured from the garment's model when it is put on.
    // A capsule's radius is measured from the body's own mesh, so the solver fits every body type:
    // around the trunk in each of Capsule.Directions directions around its bone, with its narrow
    // grooves filled, so a torso that is deep in front of the spine and shallow behind it keeps a
    // cloak on its back and the groove along the spine does not catch the cloth; the legs are about
    // round and move under the cloth, which slides around them, so their capsules are round and push
    // it straight away. A chain need not lie in its cloth: the
    // margin a particle keeps from the body is the one its cloth keeps, the profile's and how far the
    // garment's cloth lies within the particle. The trunk carries the chains, and no capsule follows
    // its shape exactly, such as the hips' capsule at the small of the back, so against the trunk's
    // capsules a particle keeps no more than it has in the bind pose: a body standing still wears its
    // garments as they were made. The legs move under the cloth, so it keeps its margin from them.
    // tools/blender/art_pipeline/cloth.py lays the chains the same way for the art pipeline's reviews.
    // Near the camera the springs run, farther away the chains only follow their targets, and far
    // away they rest. In play ClothChainScheduler steps every solver once a frame and decides how,
    // so a crowd stays cheap. It is presentation only and never reaches the server. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    [DisallowMultipleComponent]
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
            // Out of view: the chains keep their last pose and start again from their targets when
            // they are seen.
            Hidden,
        }

        private const float FrameSeconds = 1f / 60f;
        // The cloth section of art/Skeletons/Humanoid.json holds these and ClothKinematics' constants.
        internal const float FallbackRadius = 0.05f;
        // Vertices of a garment within this share of a bone's length along its chain lie at one
        // place, such as a ring of a skirt.
        internal const float PlaceSpan = 0.05f;
        // A capsule's narrow grooves are filled this many times over.
        internal const int GroovePasses = 2;

        // The body bones the cloth collides with, each with the bone its capsule ends at. The head is
        // a sphere around its own vertices.
        internal static readonly (string Bone, string End)[] CapsuleBones =
        {
            ("Hips", "Spine"), ("Spine", "Chest"), ("Chest", "UpperChest"), ("UpperChest", "Neck"), ("Neck", "Head"),
            ("Head", null), ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"),
            ("LowerLeg_R", "Foot_R"),
        };

        // In play, where models do not change, every body of a model shares what the solver measured
        // of the model, by the meshes of its parts, and every garment model what was measured of
        // its cloth: measuring takes milliseconds, and a crowd spawns many bodies. Out of play each
        // body measures its own, so a model exported again is measured again.
        private static readonly Dictionary<MeshSet, BodyShape> SharedShapes = new Dictionary<MeshSet, BodyShape>();
        private static readonly Dictionary<(GameObject Garment, ClothChainKind Kind), Dictionary<string, Vector2[]>> SharedReaches =
            new Dictionary<(GameObject Garment, ClothChainKind Kind), Dictionary<string, Vector2[]>>();

        // The profile of each kind of chain, by kind; null for a kind without cloth.
        private readonly ClothProfile?[] profiles = new ClothProfile?[(int)ClothChainKind.Hood + 1];
        private readonly List<ChainState> chains = new List<ChainState>();
        private readonly List<Capsule> capsules = new List<Capsule>();
        private readonly ClothKinematics.Envelope skirtEnvelope = new ClothKinematics.Envelope();
        private Dictionary<string, Transform> bonesByName;
        private BodyShape shape;
        private Legs legs;

        // Lets tests share measures out of play.
        internal static bool SharesMeasuresOutOfPlay { get; set; }

        private static bool SharesMeasures => Application.isPlaying || SharesMeasuresOutOfPlay;

        public Detail CurrentDetail { get; private set; } = Detail.Rest;

        public bool IsActive => chains.Count > 0;

        // The frame ClothChainScheduler last stepped the chains in, for its turns.
        internal int SteppedFrame { get; set; } = -1;

        public IReadOnlyList<Capsule> Capsules => capsules;

        // Whether the last step laid a cloak over a robe's skirt.
        public bool LiesOverSkirt => skirtEnvelope.IsBuilt;

        // Lets the chains of a kind carry cloth with the profile's behavior, such as a robe on the
        // skirt chains. The garment's model tells how far its cloth reaches beyond and within the
        // chains, so a cloak lies over a robe's skirt; without it the cloth lies on the chains. A
        // body without the chains, such as a model exported before they existed, has nothing to move.
        public void Activate(ClothChainKind kind, ClothProfile profile, GameObject garment = null)
        {
            Deactivate(kind);
            var bones = BonesByName();
            var created = new List<ChainState>();
            foreach (var chain in ClothChains.All)
            {
                if (chain.Kind == kind && ChainState.TryCreate(chain, bones, out var state))
                {
                    created.Add(state);
                }
            }

            if (created.Count == 0)
            {
                return;
            }

            profiles[(int)kind] = profile;
            if (shape == null)
            {
                shape = ShapeOf(bones);
                foreach (var capsuleShape in shape.Capsules)
                {
                    capsules.Add(new Capsule(bones[capsuleShape.Bone], capsuleShape));
                }

                legs = Legs.Of(shape.Legs, bones);
            }

            var reaches = garment != null ? ReachesOf(garment, kind) : null;
            foreach (var state in created)
            {
                state.Allow(capsules, shape);
                if (reaches != null && reaches.TryGetValue(state.Chain.Bones[0], out var reach))
                {
                    state.Reach = reach;
                }

                chains.Add(state);
            }
        }

        // Puts the chains of a kind back to their rest pose and stops moving them.
        public void Deactivate(ClothChainKind kind)
        {
            profiles[(int)kind] = null;
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

            return Array.Empty<Vector3>();
        }

        // How far the cloth at the deepest joint or tip of the active chains of a kind, where its bones
        // really are, lies inside a capsule, beyond how far it does against the trunk in the bind
        // pose, in meters; zero or less when none lies deeper.
        public float DeepestInBody(ClothChainKind kind)
        {
            var deepest = float.NegativeInfinity;
            foreach (var state in chains)
            {
                if (state.Chain.Kind == kind)
                {
                    deepest = Mathf.Max(deepest, state.DeepestInBody(capsules, ReachScale));
                }
            }

            return float.IsNegativeInfinity(deepest) ? 0f : deepest;
        }

        // How far the deepest particle of the cloak chains lies within the cloth of a robe's skirt
        // under it, its gap and the cloak cloth's reach within its chains included, after the last
        // step; zero or less when the cloak lies over it, or when no skirt was under it.
        public float DeepestUnderCloak()
        {
            var deepest = float.NegativeInfinity;
            foreach (var state in chains)
            {
                if (state.Chain.Kind == ClothChainKind.Cloak && skirtEnvelope.IsBuilt)
                {
                    deepest = Mathf.Max(deepest, state.DeepestIn(skirtEnvelope, ReachScale));
                }
            }

            return float.IsNegativeInfinity(deepest) ? 0f : deepest;
        }

        // The body in its current pose as a case of the kinematic fixtures: its capsules and legs as
        // posed, its active chains as they hang from their animated parents at rest, and what
        // ClothKinematics lays from them, which is what the Follow detail shows. Leaves the chains
        // at rest.
        public ClothKinematicCase CaptureKinematics(string name)
        {
            var captured = new ClothKinematicCase { name = name, scale = ReachScale, up = transform.up };
            foreach (var capsule in capsules)
            {
                capsule.Update();
                captured.capsules.Add(new ClothKinematicCapsule
                {
                    bone = capsule.BoneName,
                    a = capsule.A,
                    b = capsule.B,
                    front = capsule.Front,
                    side = capsule.Side,
                    axisNormal = capsule.AxisNormal,
                    scale = capsule.Scale,
                    radii = new List<float>(capsule.Radii).ToArray(),
                });
            }

            if (legs != null)
            {
                legs.Update();
                captured.hasLegs = true;
                captured.hips = legs.Pose.Hips;
                captured.forward = legs.Pose.Forward;
                captured.right = legs.Pose.Right;
                captured.ahead = legs.Pose.Ahead;
                captured.legTurns = (Quaternion[])legs.Pose.Turns.Clone();
            }

            foreach (var state in chains)
            {
                state.Rest();
                var hanging = new Vector3[state.Positions.Length];
                state.Hang(hanging);
                captured.chains.Add(ClothKinematicChain.Of(state.Chain, profiles[(int)state.Chain.Kind].GetValueOrDefault(), hanging,
                    state.Lengths, state.RestDepthsInBindPose, state.Reach, null));
            }

            var laid = ClothKinematics.Lay(captured);
            for (var index = 0; index < laid.Length; index++)
            {
                captured.chains[index].expected = laid[index];
            }

            return captured;
        }

        // Starts every chain again from where it hangs, as after a teleport.
        public void Restart()
        {
            foreach (var state in chains)
            {
                state.Started = false;
            }
        }

        // The detail the chains of a body at this distance from the camera move with.
        public static Detail DetailAt(float distance)
        {
            return distance <= NearDistance ? Detail.Springs : distance <= FarDistance ? Detail.Follow : Detail.Rest;
        }

        // One step of the solver, for the frame time and the camera the body is seen from.
        public void Simulate(float deltaTime, Vector3 cameraPosition)
        {
            Simulate(deltaTime, DetailAt(Vector3.Distance(transform.position, cameraPosition)));
        }

        // One step of the solver with the detail given, for the frame time.
        public void Simulate(float deltaTime, Detail detail)
        {
            if (chains.Count == 0)
            {
                CurrentDetail = Detail.Rest;
                return;
            }

            CurrentDetail = detail;
            if (detail == Detail.Hidden || detail == Detail.Rest)
            {
                skirtEnvelope.Clear();
                foreach (var state in chains)
                {
                    if (detail == Detail.Rest)
                    {
                        state.Rest();
                    }

                    state.Started = false;
                }

                return;
            }

            foreach (var capsule in capsules)
            {
                capsule.Update();
            }

            var frames = Mathf.Max(deltaTime, 0f) / FrameSeconds;
            if (legs != null && (HasProfile(ClothChainKind.Skirt) || HasProfile(ClothChainKind.Cloak)))
            {
                legs.Update();
            }

            // The kinds in the order their cloth lies on the body, so the skirt under a cloak is in
            // place before the cloak.
            skirtEnvelope.Clear();
            var scale = ReachScale;
            for (var kind = ClothChainKind.Skirt; kind <= ClothChainKind.Hood; kind++)
            {
                var under = kind == ClothChainKind.Cloak && skirtEnvelope.IsBuilt ? skirtEnvelope : null;
                foreach (var state in chains)
                {
                    if (state.Chain.Kind != kind)
                    {
                        continue;
                    }

                    state.Rest();
                    var profile = profiles[(int)kind].GetValueOrDefault();
                    state.Step(profile, detail == Detail.Springs, frames, deltaTime,
                        state.Chain.Beside.Count > 0 ? legs : null);
                    state.Resolve(capsules, profile.Margin, under, scale);
                    state.Apply();
                }

                if (kind == ClothChainKind.Skirt)
                {
                    BuildSkirtEnvelope();
                }
            }
        }

        private bool HasProfile(ClothChainKind kind)
        {
            return profiles[(int)kind].HasValue;
        }

        // The envelope of the skirt's cloth, when a cloak that knows its reaches lies over a skirt
        // that knows its own.
        private void BuildSkirtEnvelope()
        {
            if (legs == null || !HasReaches(ClothChainKind.Skirt) || !HasReaches(ClothChainKind.Cloak))
            {
                return;
            }

            skirtEnvelope.Begin(legs.Pose.Hips, legs.Pose.Forward, legs.Pose.Right, transform.up);
            foreach (var state in chains)
            {
                if (state.Chain.Kind == ClothChainKind.Skirt)
                {
                    skirtEnvelope.Add(state.Positions, ClothKinematics.Outward(legs.Pose, state.Chain.Angle), state.Reach, ReachScale);
                }
            }
        }

        private bool HasReaches(ClothChainKind kind)
        {
            foreach (var state in chains)
            {
                if (state.Chain.Kind == kind && state.Reach != null)
                {
                    return true;
                }
            }

            return false;
        }

        // Reaches are measured in the garment model's meters; a scaled body scales them.
        private float ReachScale
        {
            get
            {
                return LargestScale(transform.lossyScale);
            }
        }

        // The largest of a scale's axes. Two-argument Max, as the params overload allocates an
        // array on every call, and this runs for every body and capsule every step.
        internal static float LargestScale(Vector3 scale)
        {
            return Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        }

        private void OnEnable()
        {
            ClothChainScheduler.Register(this);
        }

        private void OnDisable()
        {
            ClothChainScheduler.Unregister(this);
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

        // What was measured of this body's model, shared in play.
        private BodyShape ShapeOf(Dictionary<string, Transform> bones)
        {
            var parts = BodyParts();
            var key = new MeshSet(parts);
            if (SharesMeasures && SharedShapes.TryGetValue(key, out var shared))
            {
                return shared;
            }

            var measured = BodyShape.Measure(transform, parts, bones);
            if (SharesMeasures)
            {
                SharedShapes[key] = measured;
            }

            return measured;
        }

        // The body's own skinned parts: not the renderer that draws them combined with garments.
        private List<SkinnedMeshRenderer> BodyParts()
        {
            var parts = new List<SkinnedMeshRenderer>();
            foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null && renderer.name != PlayerBodyAppearance.CombinedRendererName)
                {
                    parts.Add(renderer);
                }
            }

            return parts;
        }

        // How far a garment's cloth reaches about each chain of a kind, shared in play.
        private static Dictionary<string, Vector2[]> ReachesOf(GameObject garment, ClothChainKind kind)
        {
            if (SharesMeasures && SharedReaches.TryGetValue((garment, kind), out var shared))
            {
                return shared;
            }

            var measured = MeasureReaches(garment, kind);
            if (SharesMeasures)
            {
                SharedReaches[(garment, kind)] = measured;
            }

            return measured;
        }

        // A play without a domain reload keeps statics; models may have changed since.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ForgetSharedMeasures()
        {
            SharedShapes.Clear();
            SharedReaches.Clear();
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
                found[vertex] = dominant;
            }

            return found;
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

        // How far a garment's cloth reaches beyond each particle of its chains of a kind, the way the
        // chain faces from the hips, and how far within it, in its model's bind pose, per chain by its
        // first bone: x beyond and y within, negative where all the cloth lies beyond the chain, and
        // NaN at a particle farther than half a bone from any cloth. Every vertex that a bone of the
        // chain weighs most lies at a place along the chain, the bone's number and its share of the
        // bone's length; the vertices at one place, such as a ring of a skirt, give the farthest out
        // and in there, and a particle takes them as they run between the places on either side of
        // it. art_pipeline/cloth.py measures the same.
        private static Dictionary<string, Vector2[]> MeasureReaches(GameObject garment, ClothChainKind kind)
        {
            var found = new Dictionary<string, Vector2[]>();
            var renderer = garment.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var mesh = renderer != null ? renderer.sharedMesh : null;
            if (mesh == null || !mesh.isReadable)
            {
                return found;
            }

            var toModel = renderer.transform.localToWorldMatrix;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var heads = new Dictionary<string, Vector3>();
            for (var index = 0; index < bones.Length && index < bindposes.Length; index++)
            {
                if (bones[index] != null)
                {
                    heads[bones[index].name] = (toModel * bindposes[index].inverse).GetPosition();
                }
            }

            var up = garment.transform.up;
            var forward = garment.transform.forward;
            var right = heads.TryGetValue("UpperLeg_R", out var rightLeg) && heads.TryGetValue("UpperLeg_L", out var leftLeg)
                ? Vector3.ProjectOnPlane(rightLeg - leftLeg, up).normalized
                : garment.transform.right;

            // Every bone of the chains of the kind: its chain, its number in it, and its segment.
            var chainsOfKind = new List<ClothChain>();
            var segments = new Dictionary<string, (int Chain, int Index, Vector3 A, Vector3 B)>();
            foreach (var chain in ClothChains.All)
            {
                if (chain.Kind != kind)
                {
                    continue;
                }

                chainsOfKind.Add(chain);
                for (var index = 0; index < chain.Bones.Count; index++)
                {
                    if (!heads.TryGetValue(chain.Bones[index], out var a))
                    {
                        continue;
                    }

                    // The chains hang straight in the rest pose, so the tip bone points like the one
                    // before it, as far.
                    Vector3 b;
                    if (index + 1 < chain.Bones.Count && heads.TryGetValue(chain.Bones[index + 1], out var next))
                    {
                        b = next;
                    }
                    else if (index > 0 && heads.TryGetValue(chain.Bones[index - 1], out var before))
                    {
                        b = a + (a - before);
                    }
                    else
                    {
                        continue;
                    }

                    segments[chain.Bones[index]] = (chainsOfKind.Count - 1, index, a, b);
                }
            }

            var places = new List<float>[chainsOfKind.Count];
            var beyond = new List<float>[chainsOfKind.Count];
            var outward = new Vector3[chainsOfKind.Count];
            for (var chain = 0; chain < chainsOfKind.Count; chain++)
            {
                places[chain] = new List<float>();
                beyond[chain] = new List<float>();
                var radians = chainsOfKind[chain].Angle * Mathf.Deg2Rad;
                outward[chain] = forward * Mathf.Cos(radians) + right * Mathf.Sin(radians);
            }

            var vertices = mesh.vertices;
            var dominants = DominantBones(mesh);
            for (var vertex = 0; vertex < vertices.Length; vertex++)
            {
                var dominant = dominants[vertex];
                if (dominant < 0 || dominant >= bones.Length || bones[dominant] == null
                    || !segments.TryGetValue(bones[dominant].name, out var segment))
                {
                    continue;
                }

                var point = toModel.MultiplyPoint3x4(vertices[vertex]);
                var axis = segment.B - segment.A;
                var share = Vector3.Dot(point - segment.A, axis) / Mathf.Max(axis.sqrMagnitude, 1e-12f);
                var closest = segment.A + axis * Mathf.Clamp01(share);
                places[segment.Chain].Add(segment.Index + share);
                beyond[segment.Chain].Add(Vector3.Dot(point - closest, outward[segment.Chain]));
            }

            for (var chain = 0; chain < chainsOfKind.Count; chain++)
            {
                found[chainsOfKind[chain].Bones[0]] = AlongChain(places[chain], beyond[chain], chainsOfKind[chain].Bones.Count + 1);
            }

            return found;
        }

        // The farthest out and in at every particle of a chain, from the vertices at their places
        // along it: vertices within PlaceSpan of each other lie at one place, and a particle takes
        // the places on either side of it, linearly, or the nearest within half a bone.
        private static Vector2[] AlongChain(List<float> places, List<float> beyond, int particles)
        {
            var found = new Vector2[particles];
            for (var index = 0; index < particles; index++)
            {
                found[index] = new Vector2(float.NaN, float.NaN);
            }

            if (places.Count == 0)
            {
                return found;
            }

            var order = new int[places.Count];
            for (var index = 0; index < order.Length; index++)
            {
                order[index] = index;
            }

            Array.Sort(order, (x, y) => places[x].CompareTo(places[y]));
            var rings = new List<(float Place, float Out, float In)>();
            var start = 0;
            for (var index = 1; index <= order.Length; index++)
            {
                if (index < order.Length && places[order[index]] - places[order[start]] <= PlaceSpan)
                {
                    continue;
                }

                float sum = 0f, farthest = float.NegativeInfinity, nearest = float.PositiveInfinity;
                for (var member = start; member < index; member++)
                {
                    sum += places[order[member]];
                    farthest = Mathf.Max(farthest, beyond[order[member]]);
                    nearest = Mathf.Min(nearest, beyond[order[member]]);
                }

                rings.Add((sum / (index - start), farthest, -nearest));
                start = index;
            }

            for (var particle = 0; particle < particles; particle++)
            {
                if (particle < rings[0].Place - 0.5f || particle > rings[rings.Count - 1].Place + 0.5f)
                {
                    continue;
                }

                if (particle <= rings[0].Place)
                {
                    found[particle] = new Vector2(rings[0].Out, rings[0].In);
                    continue;
                }

                if (particle >= rings[rings.Count - 1].Place)
                {
                    var last = rings[rings.Count - 1];
                    found[particle] = new Vector2(last.Out, last.In);
                    continue;
                }

                var after = rings.FindIndex(ring => ring.Place >= particle);
                var (p0, out0, in0) = rings[after - 1];
                var (p1, out1, in1) = rings[after];
                var share = p1 > p0 ? (particle - p0) / (p1 - p0) : 0f;
                found[particle] = new Vector2(out0 + (out1 - out0) * share, in0 + (in1 - in0) * share);
            }

            return found;
        }

        // The body vertices a capsule bone weighs most, in the bone's own space in the bind pose,
        // and the capsule measured from them: the middle distance from the bone's segment in each
        // direction around it, from the body's front toward its right, or a sphere around the
        // head's own vertices.
        private sealed class CapsuleMeasure
        {
            private readonly Vector3 end;
            private readonly Vector3 front;
            private readonly Vector3 side;
            private readonly bool round;
            private readonly List<float> distances = new List<float>();
            private readonly List<float>[] directions = new List<float>[Capsule.Directions];
            private readonly List<Vector3> points = new List<Vector3>();

            public CapsuleMeasure(Vector3 end, Vector3 bodyFront, Vector3 bodyRight, bool round)
            {
                this.end = end;
                this.round = round;
                var axis = end.sqrMagnitude > 1e-12f ? end.normalized : Vector3.zero;
                front = (bodyFront - axis * Vector3.Dot(bodyFront, axis)).normalized;
                side = (bodyRight - axis * Vector3.Dot(bodyRight, axis) - front * Vector3.Dot(bodyRight, front)).normalized;
                for (var index = 0; index < directions.Length; index++)
                {
                    directions[index] = new List<float>();
                }
            }

            private bool IsSphere => end == Vector3.zero;

            public void Add(Vector3 local)
            {
                if (IsSphere)
                {
                    points.Add(local);
                    return;
                }

                var away = local - Capsule.Closest(local, Vector3.zero, end);
                var distance = away.magnitude;
                if (round)
                {
                    distances.Add(distance);
                    return;
                }

                // Only the vertices along the segment, so the buttocks below the hips' bone do not
                // widen its capsule at the small of the back.
                var along = Vector3.Dot(local, end) / end.sqrMagnitude;
                if (along <= 0f || along >= 1f)
                {
                    return;
                }

                distances.Add(distance);
                directions[Capsule.DirectionOf(away, front, side)].Add(distance);
            }

            public CapsuleShape Measured(string bone)
            {
                if (IsSphere)
                {
                    // A sphere around the middle of the head's vertices.
                    var middle = Vector3.zero;
                    foreach (var point in points)
                    {
                        middle += point;
                    }

                    middle = points.Count > 0 ? middle / points.Count : Vector3.zero;
                    foreach (var point in points)
                    {
                        distances.Add(Vector3.Distance(point, middle));
                    }

                    var radius = Median(distances);
                    return new CapsuleShape(bone, middle, middle, new[] { radius }, radius, front, side);
                }

                if (round)
                {
                    var radius = Median(distances);
                    return new CapsuleShape(bone, Vector3.zero, end, new[] { radius }, radius, front, side);
                }

                var radii = new float?[Capsule.Directions];
                for (var index = 0; index < radii.Length; index++)
                {
                    radii[index] = directions[index].Count > 0 ? Median(directions[index]) : (float?)null;
                }

                return new CapsuleShape(bone, Vector3.zero, end, WithoutGrooves(Filled(radii, Median(distances))), Median(distances),
                    front, side);
            }

            // The radii with every narrow groove between two directions filled, such as the one along
            // the spine, which would catch cloth and swing it from side to side: a radius smaller than
            // the middle of its neighbors' takes that middle, GroovePasses times over.
            private static float[] WithoutGrooves(float[] radii)
            {
                for (var pass = 0; pass < GroovePasses; pass++)
                {
                    var filled = new float[radii.Length];
                    for (var index = 0; index < radii.Length; index++)
                    {
                        var middle = (radii[(index - 1 + radii.Length) % radii.Length] + radii[(index + 1) % radii.Length]) / 2f;
                        filled[index] = Mathf.Max(radii[index], middle);
                    }

                    radii = filled;
                }

                return radii;
            }

            // The radii with every direction that no vertex lies in taken between the nearest ones
            // that some do, around the bone.
            private static float[] Filled(float?[] radii, float fallback)
            {
                var found = new float[radii.Length];
                for (var index = 0; index < radii.Length; index++)
                {
                    if (radii[index].HasValue)
                    {
                        found[index] = radii[index].Value;
                        continue;
                    }

                    int before = -1, after = -1;
                    for (var step = 1; step < radii.Length && (before < 0 || after < 0); step++)
                    {
                        if (before < 0 && radii[(index - step + radii.Length) % radii.Length].HasValue)
                        {
                            before = step;
                        }

                        if (after < 0 && radii[(index + step) % radii.Length].HasValue)
                        {
                            after = step;
                        }
                    }

                    found[index] = before < 0
                        ? fallback
                        : Mathf.Lerp(radii[(index - before + radii.Length) % radii.Length].Value,
                            radii[(index + after) % radii.Length].Value, before / (float)(before + after));
                }

                return found;
            }
        }

        // What the solver measures of a body model, in the body's own space and in its bones' spaces:
        // the bind pose of its bones, its capsules, its legs, and how deep the particles of each
        // chain lie in the trunk's capsules at rest. Every body of one model measures the same.
        private sealed class BodyShape
        {
            private BodyShape(Dictionary<string, Matrix4x4> bindPose, List<CapsuleShape> capsules, LegsShape legs)
            {
                BindPose = bindPose;
                Capsules = capsules;
                Legs = legs;
            }

            // Every bone of the body's parts in the bind pose, in the body's own space.
            public Dictionary<string, Matrix4x4> BindPose { get; }

            public IReadOnlyList<CapsuleShape> Capsules { get; }

            // Null for a body without legs.
            public LegsShape Legs { get; }

            // How deep each chain's particles lie in each capsule at rest, by the chain's first bone,
            // measured when the chain first goes on.
            public Dictionary<string, float[]> RestDepths { get; } = new Dictionary<string, float[]>();

            public static BodyShape Measure(Transform body, IReadOnlyList<SkinnedMeshRenderer> parts, Dictionary<string, Transform> bones)
            {
                var bindPose = BindPoseOf(body, parts);
                return new BodyShape(bindPose, MeasureCapsules(body, parts, bones, bindPose), ClothChainSolver.Legs.Measure(bindPose));
            }

            private static Dictionary<string, Matrix4x4> BindPoseOf(Transform body, IReadOnlyList<SkinnedMeshRenderer> parts)
            {
                var toBody = body.worldToLocalMatrix;
                var found = new Dictionary<string, Matrix4x4>();
                foreach (var renderer in parts)
                {
                    var bindposes = renderer.sharedMesh.bindposes;
                    var rendererBones = renderer.bones;
                    for (var index = 0; index < rendererBones.Length && index < bindposes.Length; index++)
                    {
                        if (rendererBones[index] != null && !found.ContainsKey(rendererBones[index].name))
                        {
                            found[rendererBones[index].name] = toBody * renderer.transform.localToWorldMatrix * bindposes[index].inverse;
                        }
                    }
                }

                return found;
            }

            // Every capsule's radius is the middle distance of the body vertices that its bone moves
            // most from the bone's segment in the bind pose, in each direction around the bone, so the
            // cloth lies on this body's own shape.
            private static List<CapsuleShape> MeasureCapsules(Transform body, IReadOnlyList<SkinnedMeshRenderer> parts,
                Dictionary<string, Transform> bones, Dictionary<string, Matrix4x4> bindPose)
            {
                // The body's front and its right, toward the right leg and level, in the bind pose.
                var front = Vector3.forward;
                var right = bindPose.TryGetValue("UpperLeg_R", out var rightLeg) && bindPose.TryGetValue("UpperLeg_L", out var leftLeg)
                    ? Vector3.ProjectOnPlane(rightLeg.GetPosition() - leftLeg.GetPosition(), Vector3.up).normalized
                    : Vector3.right;
                var measures = new Dictionary<string, CapsuleMeasure>();
                foreach (var (boneName, endName) in CapsuleBones)
                {
                    if (!bones.TryGetValue(boneName, out var bone))
                    {
                        continue;
                    }

                    var end = endName != null && bones.TryGetValue(endName, out var endBone) ? endBone.localPosition : Vector3.zero;
                    // The body's front and right in the bone's own space in the bind pose.
                    var toBone = bindPose.TryGetValue(boneName, out var boneRest)
                        ? boneRest.inverse
                        : bone.worldToLocalMatrix * body.localToWorldMatrix;
                    measures[boneName] = new CapsuleMeasure(end, toBone.MultiplyVector(front), toBone.MultiplyVector(right),
                        Capsule.IsLeg(boneName));
                }

                foreach (var renderer in parts)
                {
                    CollectDistances(renderer, measures);
                }

                var found = new List<CapsuleShape>();
                foreach (var (boneName, _) in CapsuleBones)
                {
                    if (measures.TryGetValue(boneName, out var measure))
                    {
                        found.Add(measure.Measured(boneName));
                    }
                }

                return found;
            }

            private static void CollectDistances(SkinnedMeshRenderer renderer, Dictionary<string, CapsuleMeasure> measures)
            {
                var mesh = renderer.sharedMesh;
                if (!mesh.isReadable)
                {
                    return;
                }

                var bones = renderer.bones;
                var bindposes = mesh.bindposes;
                var vertices = mesh.vertices;
                var dominants = DominantBones(mesh);
                for (var vertex = 0; vertex < vertices.Length; vertex++)
                {
                    var dominant = dominants[vertex];
                    if (dominant < 0 || dominant >= bones.Length || bones[dominant] == null
                        || !measures.TryGetValue(bones[dominant].name, out var measure))
                    {
                        continue;
                    }

                    // The vertex in the bone's own space at the bind pose.
                    measure.Add(bindposes[dominant].MultiplyPoint3x4(vertices[vertex]));
                }
            }
        }

        // A body model's capsule as measured, in its bone's own space.
        internal sealed class CapsuleShape
        {
            public CapsuleShape(string bone, Vector3 a, Vector3 b, float[] radii, float radius, Vector3 front, Vector3 side)
            {
                Bone = bone;
                A = a;
                B = b;
                Radii = radii;
                Radius = radius;
                Front = front;
                Side = side;
                foreach (var each in radii)
                {
                    Widest = Mathf.Max(Widest, each);
                }
            }

            public string Bone { get; }

            public Vector3 A { get; }

            public Vector3 B { get; }

            // The radius in each direction around the bone, or one for a sphere or a leg.
            public float[] Radii { get; }

            public float Radius { get; }

            public float Widest { get; }

            public Vector3 Front { get; }

            public Vector3 Side { get; }
        }

        // Where a body model's thighs and shins point against its hips at rest, and the hips'
        // forward and right, in the hips' own space.
        private sealed class LegsShape
        {
            public LegsShape(Vector3[] restInHips, Vector3 forwardInHips, Vector3 rightInHips)
            {
                RestInHips = restInHips;
                ForwardInHips = forwardInHips;
                RightInHips = rightInHips;
            }

            public Vector3[] RestInHips { get; }

            public Vector3 ForwardInHips { get; }

            public Vector3 RightInHips { get; }
        }

        // The key of a body model: the meshes of its parts, in their order.
        private sealed class MeshSet : IEquatable<MeshSet>
        {
            private readonly Mesh[] meshes;
            private readonly int hash;

            public MeshSet(IReadOnlyList<SkinnedMeshRenderer> parts)
            {
                meshes = new Mesh[parts.Count];
                hash = parts.Count;
                for (var index = 0; index < meshes.Length; index++)
                {
                    meshes[index] = parts[index].sharedMesh;
                    hash = hash * 31 + meshes[index].GetHashCode();
                }
            }

            public bool Equals(MeshSet other)
            {
                if (other == null || other.meshes.Length != meshes.Length)
                {
                    return false;
                }

                for (var index = 0; index < meshes.Length; index++)
                {
                    if (!ReferenceEquals(meshes[index], other.meshes[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as MeshSet);
            }

            public override int GetHashCode()
            {
                return hash;
            }
        }

        // A capsule around a body bone, in the bone's own space, placed in the world every step: its
        // segment, and the body's distance from it in each of Directions directions around it, from
        // the body's front toward its right, or one radius for a sphere.
        public sealed class Capsule
        {
            public const int Directions = 16;

            private readonly Transform bone;
            private readonly CapsuleShape shape;
            private Vector3 front;
            private Vector3 side;
            private float scale;
            // The largest radius in any direction, scaled: no point farther from the axis lies within.
            private float widest;
            private Vector3 axis;
            private float axisLengthSquared;
            private Vector3 axisNormal;

            internal Capsule(Transform bone, CapsuleShape shape)
            {
                this.bone = bone;
                this.shape = shape;
                Update();
            }

            private Capsule(CapsuleShape shape)
            {
                this.shape = shape;
            }

            // A capsule placed as given, without a bone, as the kinematic fixtures hold it.
            internal static Capsule Posed(string boneName, float[] radii, Vector3 a, Vector3 b, Vector3 front, Vector3 side, float scale,
                Vector3 axisNormal)
            {
                var sorted = (float[])radii.Clone();
                Array.Sort(sorted);
                var capsule = new Capsule(new CapsuleShape(boneName, a, b, radii, sorted.Length > 0 ? sorted[sorted.Length / 2] : 0f, front, side));
                capsule.Place(a, b, front, side, scale, axisNormal);
                return capsule;
            }

            public string BoneName => shape.Bone;

            // Whether the capsule is the trunk's, which carries the chains, rather than a leg's, which
            // moves under the cloth.
            public bool IsTrunk => !IsLeg(shape.Bone);

            public Vector3 A { get; private set; }

            public Vector3 B { get; private set; }

            // The middle distance of the body from the bone, over every direction.
            public float Radius { get; private set; }

            // The body's front and side around the capsule, as posed.
            public Vector3 Front => front;

            public Vector3 Side => side;

            public float Scale => scale;

            // The way out of a point on the capsule's axis: the way its bone faces.
            public Vector3 AxisNormal => axisNormal;

            public IReadOnlyList<float> Radii => shape.Radii;

            public void Update()
            {
                if (bone == null)
                {
                    return;
                }

                Place(bone.TransformPoint(shape.A), bone.TransformPoint(shape.B), bone.TransformDirection(shape.Front),
                    bone.TransformDirection(shape.Side), LargestScale(bone.lossyScale), bone.forward);
            }

            private void Place(Vector3 a, Vector3 b, Vector3 posedFront, Vector3 posedSide, float posedScale, Vector3 posedAxisNormal)
            {
                A = a;
                B = b;
                axis = B - A;
                axisLengthSquared = axis.sqrMagnitude;
                scale = posedScale;
                Radius = shape.Radius * scale;
                widest = shape.Widest * scale;
                front = posedFront;
                side = posedSide;
                axisNormal = posedAxisNormal;
            }

            // The radius toward a direction from the capsule's axis, blended between the two
            // directions around it that were measured.
            public float RadiusToward(Vector3 away)
            {
                return RadiusAlong(away, front, side) * scale;
            }

            // How far a point lies within the capsule and the margin, and the way out of it.
            public float Penetration(Vector3 point, float margin, out Vector3 normal)
            {
                var closest = Closest(point, A, B);
                var away = point - closest;
                var length = away.magnitude;
                // A point on the axis leaves the way the capsule's bone faces, never through it.
                normal = length > 1e-6f ? away / length : axisNormal;
                return RadiusToward(away) + margin - length;
            }

            // Penetration for a point that may well lie outside: zero, without the way out, for a
            // point beyond the capsule's widest radius and the margin, which lies outside for certain.
            public float PenetrationIfNear(Vector3 point, float margin, out Vector3 normal)
            {
                normal = Vector3.zero;
                var reach = widest + margin;
                if (reach <= 0f)
                {
                    return 0f;
                }

                // The distance from the segment in plain floats, as Closest takes it: this runs for
                // every particle and capsule several times a step.
                var x = point.x - A.x;
                var y = point.y - A.y;
                var z = point.z - A.z;
                var t = axisLengthSquared < 1e-12f ? 0f : Mathf.Clamp01((x * axis.x + y * axis.y + z * axis.z) / axisLengthSquared);
                x -= axis.x * t;
                y -= axis.y * t;
                z -= axis.z * t;
                if (x * x + y * y + z * z >= reach * reach)
                {
                    return 0f;
                }

                return Penetration(point, margin, out normal);
            }

            // How far a point lies within the capsule when its bone has the given matrix, such as in
            // the bind pose.
            public float DepthAt(Matrix4x4 boneMatrix, Vector3 point)
            {
                var a = boneMatrix.MultiplyPoint3x4(shape.A);
                var b = boneMatrix.MultiplyPoint3x4(shape.B);
                var away = point - Closest(point, a, b);
                var matrixScale = LargestScale(boneMatrix.lossyScale);
                var radius = RadiusAlong(away, boneMatrix.MultiplyVector(shape.Front), boneMatrix.MultiplyVector(shape.Side));
                return radius * matrixScale - away.magnitude;
            }

            // The direction around a bone that an offset from it lies in, from the front toward the
            // side.
            public static bool IsLeg(string boneName)
            {
                return boneName.StartsWith("UpperLeg", StringComparison.Ordinal) || boneName.StartsWith("LowerLeg", StringComparison.Ordinal);
            }

            public static int DirectionOf(Vector3 away, Vector3 towardFront, Vector3 towardSide)
            {
                var angle = Mathf.Atan2(Vector3.Dot(away, towardSide), Vector3.Dot(away, towardFront));
                return Mathf.FloorToInt((angle + Mathf.PI) / (2f * Mathf.PI) * Directions) % Directions;
            }

            public static Vector3 Closest(Vector3 point, Vector3 a, Vector3 b)
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

            private float RadiusAlong(Vector3 away, Vector3 towardFront, Vector3 towardSide)
            {
                var radii = shape.Radii;
                var count = radii.Length;
                if (count == 1)
                {
                    return radii[0];
                }

                var angle = Mathf.Atan2(Vector3.Dot(away, towardSide), Vector3.Dot(away, towardFront));
                var position = (angle + Mathf.PI) / (2f * Mathf.PI) * count - 0.5f;
                var lower = Mathf.FloorToInt(position);
                var share = position - lower;
                return radii[((lower % count) + count) % count] * (1f - share)
                    + radii[(((lower + 1) % count) + count) % count] * share;
            }
        }

        // The legs skirts and cloaks follow: where each thigh and shin points against the hips at
        // rest, measured from the body's bind pose, and every step their pose for ClothKinematics:
        // how each turns against the posed hips, the hips' forward and right axes, and how far the
        // right knee lies ahead of the left.
        private sealed class Legs
        {
            private static readonly (string Bone, string End)[] Parts =
            {
                ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"), ("LowerLeg_R", "Foot_R"),
            };

            private readonly Transform hips;
            private readonly Transform[] parts;
            private readonly Transform[] ends;
            private readonly Vector3[] restInHips;
            private readonly Vector3 forwardInHips;
            private readonly Vector3 rightInHips;

            private Legs(Transform hips, Transform[] parts, Transform[] ends, Vector3[] restInHips, Vector3 forwardInHips,
                Vector3 rightInHips)
            {
                this.hips = hips;
                this.parts = parts;
                this.ends = ends;
                this.restInHips = restInHips;
                this.forwardInHips = forwardInHips;
                this.rightInHips = rightInHips;
            }

            public ClothKinematics.LegPose Pose { get; } = new ClothKinematics.LegPose();

            // The legs of a body, on its own bones, or null for a body without them.
            public static Legs Of(LegsShape shape, Dictionary<string, Transform> bones)
            {
                if (shape == null || !bones.TryGetValue("Hips", out var hips))
                {
                    return null;
                }

                var parts = new Transform[Parts.Length];
                var ends = new Transform[Parts.Length];
                for (var index = 0; index < Parts.Length; index++)
                {
                    var (bone, end) = Parts[index];
                    if (!bones.TryGetValue(bone, out parts[index]) || !bones.TryGetValue(end, out ends[index]))
                    {
                        return null;
                    }
                }

                return new Legs(hips, parts, ends, shape.RestInHips, shape.ForwardInHips, shape.RightInHips);
            }

            // Where the legs of a body point against its hips at rest, from its bind pose in its own
            // space, or null for a body without them.
            public static LegsShape Measure(Dictionary<string, Matrix4x4> rest)
            {
                if (!rest.TryGetValue("Hips", out var hipsRest))
                {
                    return null;
                }

                var fromHips = Quaternion.Inverse(hipsRest.rotation);
                var directions = new Vector3[Parts.Length];
                for (var index = 0; index < Parts.Length; index++)
                {
                    var (bone, end) = Parts[index];
                    if (!rest.TryGetValue(bone, out var boneRest) || !rest.TryGetValue(end, out var endRest))
                    {
                        return null;
                    }

                    directions[index] = fromHips * (endRest.GetPosition() - boneRest.GetPosition()).normalized;
                }

                // Toward the right leg, level.
                var across = Vector3.ProjectOnPlane(rest["UpperLeg_R"].GetPosition() - rest["UpperLeg_L"].GetPosition(), Vector3.up);
                return new LegsShape(directions, fromHips * Vector3.forward, fromHips * across.normalized);
            }

            public void Update()
            {
                for (var index = 0; index < parts.Length; index++)
                {
                    Pose.Turns[index] = Quaternion.FromToRotation(hips.rotation * restInHips[index], ends[index].position - parts[index].position);
                }

                Pose.Forward = hips.rotation * forwardInHips;
                Pose.Right = hips.rotation * rightInHips;
                Pose.Hips = hips.position;
                Pose.Ahead = Vector3.Dot(parts[ClothKinematics.LegPose.ShinR].position - parts[ClothKinematics.LegPose.ShinL].position,
                    Pose.Forward);
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
            // How far each particle lies within each of the trunk's capsules in the bind pose,
            // unclamped, and negative infinity for the legs': against the trunk it keeps no more than
            // it has there. By particle, then capsule.
            private float[] restDepths;
            private int capsuleCount;
            // The margin each particle keeps from each capsule in this step, by particle, then capsule.
            private float[] margins = new float[0];
            // Whether the bones have their rest rotations, so resting them again writes nothing.
            private bool resting;

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

            // How far the garment's cloth reaches beyond each particle and within it, or null when the
            // garment is not known; NaN at a particle without cloth.
            public Vector2[] Reach { get; set; }

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

            // Takes how far each particle lies within each capsule in the bind pose, where the chain
            // hangs at rest from its parent, from the body's model: every body of it has the same.
            public void Allow(List<Capsule> capsules, BodyShape shape)
            {
                capsuleCount = capsules.Count;
                if (!shape.RestDepths.TryGetValue(Chain.Bones[0], out restDepths))
                {
                    restDepths = RestDepths(capsules, shape.BindPose);
                    shape.RestDepths[Chain.Bones[0]] = restDepths;
                }
            }

            // How far each particle lies within each capsule in the bind pose, in the body's own
            // space.
            private float[] RestDepths(List<Capsule> capsules, Dictionary<string, Matrix4x4> bindPose)
            {
                var depths = new float[Positions.Length * capsuleCount];
                var parent = bones[0].parent;
                if (parent == null || !bindPose.TryGetValue(parent.name, out var matrix))
                {
                    for (var index = 0; index < depths.Length; index++)
                    {
                        depths[index] = float.NegativeInfinity;
                    }

                    return depths;
                }

                var points = new Vector3[Positions.Length];
                for (var index = 0; index < bones.Length; index++)
                {
                    matrix *= Matrix4x4.TRS(bones[index].localPosition, restRotations[index], bones[index].localScale);
                    points[index] = matrix.GetPosition();
                }

                points[bones.Length] = matrix.MultiplyPoint3x4(childOffsets[bones.Length - 1]);
                for (var capsule = 0; capsule < capsules.Count; capsule++)
                {
                    var boneMatrix = Matrix4x4.identity;
                    var known = capsules[capsule].IsTrunk && bindPose.TryGetValue(capsules[capsule].BoneName, out boneMatrix);
                    for (var index = 0; index < Positions.Length; index++)
                    {
                        depths[index * capsuleCount + capsule] = known
                            ? capsules[capsule].DepthAt(boneMatrix, points[index])
                            : float.NegativeInfinity;
                    }
                }

                return depths;
            }

            public void Rest()
            {
                if (resting)
                {
                    return;
                }

                for (var index = 0; index < bones.Length; index++)
                {
                    bones[index].localRotation = restRotations[index];
                }

                resting = true;
            }

            // Where every particle hangs from the animated parent with the bones at rest, or follows
            // the legs, and the particles' move toward it: inertia, gravity, and the spring with
            // springs on, straight to it without them.
            public void Step(ClothProfile profile, bool springs, float frames, float deltaTime, Legs legs)
            {
                Hang(targets);
                if (legs != null)
                {
                    ClothKinematics.FollowLegs(targets, lengths, Chain.Beside, Chain.Angle, legs.Pose, profile.Slack);
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

            // Where every particle hangs from the chain's animated parent with the bones at rest.
            public void Hang(Vector3[] into)
            {
                into[0] = bones[0].position;
                for (var index = 0; index < bones.Length; index++)
                {
                    into[index + 1] = bones[index].TransformPoint(childOffsets[index]);
                }
            }

            public float[] Lengths => lengths;

            // How far each particle lies within each capsule in the bind pose, by particle, then
            // capsule, negative infinity for no limit; null before Allow.
            public float[] RestDepthsInBindPose => restDepths;

            // Moves every particle out of the capsules and out of the cloth under it at its bone's
            // length from the one before; see ClothKinematics.Resolve.
            public void Resolve(List<Capsule> capsules, float margin, ClothKinematics.Envelope under, float scale)
            {
                if (margins.Length != Positions.Length * capsules.Count)
                {
                    margins = new float[Positions.Length * capsules.Count];
                }

                ClothKinematics.Margins(margin, Reach, restDepths, capsules.Count, scale, margins);
                ClothKinematics.Resolve(Positions, targets, lengths, capsules, margins, Reach, under, scale);
            }

            // The margin a particle keeps from a capsule: the one its cloth keeps, the profile's and
            // how far the cloth lies within the particle, and from the trunk no more than it has in the
            // bind pose, measured in the body's own space and so scaled with it.
            private float MarginFrom(int index, int capsule, float margin, float scale)
            {
                var own = margin + ClothKinematics.Within(Reach, index, scale);
                return restDepths == null ? own : Mathf.Min(own, -restDepths[index * capsuleCount + capsule] * scale);
            }

            // Turns every bone, from the root to the tip, to point at its next particle.
            public void Apply()
            {
                resting = false;
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

            // How far the cloth at the deepest joint or tip, where the bones really are, lies inside a
            // capsule, beyond how far it does against the trunk in the bind pose.
            public float DeepestInBody(List<Capsule> capsules, float scale)
            {
                var deepest = float.NegativeInfinity;
                for (var index = 1; index <= bones.Length; index++)
                {
                    var point = index < bones.Length ? bones[index].position : bones[index - 1].TransformPoint(childOffsets[index - 1]);
                    for (var capsule = 0; capsule < capsules.Count; capsule++)
                    {
                        deepest = Mathf.Max(deepest, capsules[capsule].Penetration(point, MarginFrom(index, capsule, 0f, scale), out _));
                    }
                }

                return deepest;
            }

            // How far the deepest particle with cloth lies within the cloth under it, the gap and its
            // own cloth's reach within it included.
            public float DeepestIn(ClothKinematics.Envelope under, float scale)
            {
                var deepest = float.NegativeInfinity;
                for (var index = 1; Reach != null && index < Positions.Length; index++)
                {
                    if (!float.IsNaN(Reach[index].y))
                    {
                        deepest = Mathf.Max(deepest, under.Penetration(Positions[index], (ClothKinematics.LayerGap + Reach[index].y) * scale, out _));
                    }
                }

                return deepest;
            }
        }
    }
}
