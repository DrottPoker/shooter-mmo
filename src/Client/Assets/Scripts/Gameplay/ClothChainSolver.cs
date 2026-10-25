using System;
using System.Collections.Generic;
using Mmorpg.Client.Cloth;
using Unity.Mathematics;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Lays the cloth chains of a body for the garments that hang on them, in the target pose of
    // docs/EQUIPMENT_VISUALS_DESIGN.md: every chain hangs from its animated parent, its bones beside
    // a thigh or a shin follow the legs, and each bone swings outward, the way its chain faces, by
    // the smallest angle that keeps its cloth and the cloth toward its neighbours outside the trunk
    // and the legs, for a cloak also the arms and the outer side of a robe's skirt under it; on the
    // nearest bodies springs swing the cloth on that pose, within limits. The laying is ClothPose
    // and ClothSprings, run by a ClothWorld in Burst jobs: in play the world of
    // ClothChainScheduler, which lays every body in view each frame, and out of play, as in the
    // Wardrobe and the tests, one of the body's own that Simulate steps. This measures the body
    // and its garments once and describes them to the world: the capsules around its bones, from
    // the body's own mesh in the bind pose, around the trunk in each of Capsule.Directions
    // directions with its narrow grooves filled and round around the limbs; where its legs point
    // against its hips; and how far each garment's cloth reaches beyond and within its chains. It
    // is presentation only and never reaches the server.
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class ClothChainSolver : MonoBehaviour
    {
        // Beyond this distance from the camera the chains rest.
        public const float FarDistance = 60f;

        // The cloth section of art/Skeletons/Humanoid.json holds these and ClothPose's constants.
        internal const float FallbackRadius = 0.05f;
        // Vertices of a garment within this share of a bone's length along its chain lie at one
        // place, such as a ring of a skirt.
        internal const float PlaceSpan = 0.05f;
        // A capsule's narrow grooves are filled this many times over.
        internal const int GroovePasses = 2;
        // A limb's round capsule reaches out to this share of its vertices, so the calf, the heel, and
        // the back of the upper arm do not stand out of it.
        internal const float LimbShare = 0.9f;

        // The body bones the cloth collides with, each with the bone its capsule ends at. The head is
        // a sphere around its own vertices.
        internal static readonly (string Bone, string End)[] CapsuleBones =
        {
            ("Hips", "Spine"), ("Spine", "Chest"), ("Chest", "UpperChest"), ("UpperChest", "Neck"), ("Neck", "Head"),
            ("Head", null), ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"),
            ("LowerLeg_R", "Foot_R"), ("Foot_L", "Toes_L"), ("Foot_R", "Toes_R"), ("UpperArm_L", "LowerArm_L"),
            ("LowerArm_L", "Hand_L"), ("UpperArm_R", "LowerArm_R"), ("LowerArm_R", "Hand_R"),
        };

        // The bones a body's world reads, by slot: the body itself, the hips and the legs, then the
        // other capsule bones and the chains' parents.
        private static readonly string[] SlotBones =
        {
            null, "Hips", "UpperLeg_L", "LowerLeg_L", "Foot_L", "UpperLeg_R", "LowerLeg_R", "Foot_R", "Spine", "Chest", "UpperChest",
            "Neck", "Head", "UpperArm_L", "LowerArm_L", "UpperArm_R", "LowerArm_R",
        };

        // The block every body takes in a cloth world: the Humanoid skeleton's slots, capsules, and
        // chains.
        public static readonly ClothBodyLayout Layout = BuildLayout();

        // In play, where models do not change, every body of a model shares what the solver measured
        // of the model, by the meshes of its parts, and every garment model what was measured of
        // its cloth: measuring takes milliseconds, and a crowd spawns many bodies. Out of play each
        // body measures its own, so a model exported again is measured again.
        private static readonly Dictionary<MeshSet, BodyShape> SharedShapes = new Dictionary<MeshSet, BodyShape>();
        private static readonly Dictionary<(GameObject Garment, ClothChainKind Kind), Dictionary<string, Vector2[]>> SharedReaches =
            new Dictionary<(GameObject Garment, ClothChainKind Kind), Dictionary<string, Vector2[]>>();

        // The profile and the garment's reaches of each kind of chain, by kind; no profile for a kind
        // without cloth.
        private readonly ClothProfile?[] profiles = new ClothProfile?[(int)ClothChainKind.Hood + 1];
        private readonly Dictionary<string, Vector2[]>[] reaches = new Dictionary<string, Vector2[]>[(int)ClothChainKind.Hood + 1];
        private readonly List<Capsule> capsules = new List<Capsule>();
        // The chains the world lays, in its order.
        private readonly List<ClothChain> laid = new List<ClothChain>();
        private readonly Dictionary<Transform, (Vector3 Position, Quaternion Rotation, Vector3 Scale)> rests =
            new Dictionary<Transform, (Vector3 Position, Quaternion Rotation, Vector3 Scale)>();
        private Dictionary<string, Transform> bonesByName;
        private BodyShape shape;
        private ClothWorld world;
        private ClothWorld ownWorld;
        private int handle = -1;

        // Lets tests share measures out of play.
        internal static bool SharesMeasuresOutOfPlay { get; set; }

        // Moves the body out of play into a world shared with other bodies, as tests of a crowd do; the
        // body starts its springs again there, and the world's owner disposes it.
        internal void ShareWorld(ClothWorld shared)
        {
            if (world != null && handle >= 0)
            {
                world.Remove(handle);
            }

            handle = -1;
            ownWorld?.Dispose();
            ownWorld = null;
            world = shared;
            Describe();
        }

        private static bool SharesMeasures => Application.isPlaying || SharesMeasuresOutOfPlay;

        public ClothDetail CurrentDetail { get; private set; } = ClothDetail.Rest;

        public bool IsActive => laid.Count > 0;

        // What the body's springs cost of the budget: a robe and a cloak count one each, and a hood
        // alone one.
        public int SpringCost
        {
            get
            {
                var cost = (profiles[(int)ClothChainKind.Skirt].HasValue ? 1 : 0) + (profiles[(int)ClothChainKind.Cloak].HasValue ? 1 : 0);
                return Math.Max(cost, IsActive ? 1 : 0);
            }
        }

        // How far the body's springs are faded in, from 0 to 1, and whether its cloth sleeps on its
        // target pose.
        public float SpringWeight => world != null && handle >= 0 ? world.SpringWeightOf(handle) : 0f;

        public bool Sleeps => world != null && handle >= 0 && world.SleepsOf(handle);

        public IReadOnlyList<Capsule> Capsules => capsules;

        // The world that lays the body and its handle there, for ClothChainScheduler.
        internal ClothWorld World => world;

        internal int Handle => handle;

        // Lets the chains of a kind carry cloth with the profile's behavior, such as a robe on the
        // skirt chains. The garment's model tells how far its cloth reaches beyond and within the
        // chains, so a cloak lies over a robe's skirt; without it the cloth lies on the chains. A
        // body without the chains, such as a model exported before they existed, has nothing to move.
        public void Activate(ClothChainKind kind, ClothProfile profile, GameObject garment = null)
        {
            var bones = BonesByName();
            if (!HasChains(kind, bones))
            {
                Deactivate(kind);
                return;
            }

            profiles[(int)kind] = profile;
            reaches[(int)kind] = garment != null ? ReachesOf(garment, kind) : null;
            Describe();
        }

        // Gives the active chains of a kind another profile, keeping their garment's reach, as the
        // Wardrobe does while the profiles are tuned; the springs start again from the target.
        public void Tune(ClothChainKind kind, ClothProfile profile)
        {
            if (!profiles[(int)kind].HasValue)
            {
                return;
            }

            profiles[(int)kind] = profile;
            Describe();
        }

        // The profile the chains of a kind move with, or null for a kind without cloth.
        public ClothProfile? ProfileOf(ClothChainKind kind) => profiles[(int)kind];

        // Tells the solver that the body jumped, as in a teleport, a respawn, or a hard correction:
        // its cloth is carried along rigidly rather than swung by the jump. Jumps are also found on
        // their own; see docs/EQUIPMENT_VISUALS_DESIGN.md.
        public void NotifyDiscontinuity()
        {
            if (world != null && handle >= 0)
            {
                world.NotifyDiscontinuity(handle);
            }
        }

        // Starts the body's springs again from its target pose, as when a motion starts over, so
        // every run of it swings alike.
        public void RestartSprings()
        {
            if (world != null && handle >= 0)
            {
                world.Restart(handle);
            }
        }

        // Tells every solver on a body under a root that the body jumped.
        public static void NotifyDiscontinuity(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            foreach (var solver in root.GetComponentsInChildren<ClothChainSolver>())
            {
                solver.NotifyDiscontinuity();
            }
        }

        // Puts the chains of a kind back to their rest pose and stops moving them.
        public void Deactivate(ClothChainKind kind)
        {
            if (!profiles[(int)kind].HasValue)
            {
                return;
            }

            profiles[(int)kind] = null;
            reaches[(int)kind] = null;
            RestChains(kind);
            Describe();
        }

        // The particles of a chain, from its root to its tip, in world space, as the last step laid
        // them; empty for a chain that is not active.
        public IReadOnlyList<Vector3> ParticlesOf(ClothChain chain)
        {
            var index = laid.IndexOf(chain);
            if (world == null || handle < 0 || index < 0)
            {
                return Array.Empty<Vector3>();
            }

            var found = new List<Vector3>();
            world.ParticlesOf(handle, index, found);
            return found;
        }

        // How far the last step's collision swung a chain's bones out of the body, from a bone on, at
        // most, in degrees: where the legs push the cloth.
        public float PushOf(ClothChain chain, int fromBone = 0)
        {
            var index = laid.IndexOf(chain);
            return world == null || handle < 0 || index < 0 ? 0f : world.PushOf(handle, index, fromBone) * Mathf.Rad2Deg;
        }

        // Where the last step laid a chain in its target pose, which its springs swing on.
        public IReadOnlyList<Vector3> TargetsOf(ClothChain chain)
        {
            var index = laid.IndexOf(chain);
            if (world == null || handle < 0 || index < 0)
            {
                return Array.Empty<Vector3>();
            }

            var found = new List<Vector3>();
            world.TargetsOf(handle, index, found);
            return found;
        }

        // The body as the last step laid it, as a case of the kinematic fixtures: its capsules as
        // posed and in the bind pose, its legs, its active chains as they hang from their animated
        // parents at rest and in the bind pose, and what ClothPose laid.
        public ClothKinematicCase CaptureKinematics(string name)
        {
            if (world == null || handle < 0)
            {
                return new ClothKinematicCase { name = name };
            }

            return ClothKinematicCase.Of(name, world.Snapshot(handle), capsules, laid);
        }

        // The most the chains of a body at this distance from the camera move with: springs, which
        // ClothChainScheduler gives only the bodies of its budget, or rest beyond the far distance.
        public static ClothDetail DetailAt(float distance)
        {
            return distance <= FarDistance ? ClothDetail.Spring : ClothDetail.Rest;
        }

        // One step out of play, for the frame time and the camera the body is seen from: alone, a
        // body near enough has its springs.
        public void Simulate(float deltaTime, Vector3 cameraPosition)
        {
            Simulate(deltaTime, DetailAt(Vector3.Distance(transform.position, cameraPosition)));
        }

        // One step out of play with the detail given, on the body's own world; in play
        // ClothChainScheduler steps every body.
        public void Simulate(float deltaTime, ClothDetail detail)
        {
            if (!IsActive)
            {
                CurrentDetail = ClothDetail.Rest;
                return;
            }

            CurrentDetail = detail;
            if (Application.isPlaying || world != ownWorld || world == null)
            {
                return;
            }

            world.SetDetail(handle, detail);
            world.Step(deltaTime);
        }

        // Sets the detail ClothChainScheduler chose for the next step of its world.
        internal void ChooseDetail(ClothDetail detail)
        {
            CurrentDetail = detail;
            world.SetDetail(handle, detail);
        }

        // The largest of a scale's axes. Two-argument Max, as the params overload allocates an
        // array on every call.
        internal static float LargestScale(Vector3 scale)
        {
            return Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        }

        // A chain's kinds in the order the world lays them, around the body: the skirt's ring from the
        // front toward the right, the cloak's sheet from the left across the back to the right.
        internal static float RingOrder(ClothChain chain)
        {
            return chain.Kind == ClothChainKind.Cloak ? Mathf.DeltaAngle(chain.Angle, 180f) : Mathf.Repeat(chain.Angle, 360f);
        }

        private static ClothBodyLayout BuildLayout()
        {
            var bones = 0;
            foreach (var chain in ClothChains.All)
            {
                bones += chain.Bones.Count;
            }

            return new ClothBodyLayout
            {
                Slots = SlotBones.Length,
                Capsules = CapsuleBones.Length,
                Radii = Capsule.Directions,
                Chains = ClothChains.All.Count,
                Bones = bones,
                Particles = bones + ClothChains.All.Count,
            };
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                ClothChainScheduler.Register(this);
            }

            if (IsActive)
            {
                Describe();
            }
        }

        private void OnDisable()
        {
            if (world != null && handle >= 0)
            {
                world.Remove(handle);
            }

            handle = -1;
            world = null;
            ClothChainScheduler.Unregister(this);
            ownWorld?.Dispose();
            ownWorld = null;
            foreach (ClothChainKind kind in Enum.GetValues(typeof(ClothChainKind)))
            {
                if (profiles[(int)kind].HasValue)
                {
                    RestChains(kind);
                }
            }
        }

        private static bool HasChains(ClothChainKind kind, Dictionary<string, Transform> bones)
        {
            foreach (var chain in ClothChains.All)
            {
                if (chain.Kind != kind)
                {
                    continue;
                }

                var all = bones.ContainsKey(chain.Parent);
                foreach (var bone in chain.Bones)
                {
                    all &= bones.ContainsKey(bone);
                }

                if (all)
                {
                    return true;
                }
            }

            return false;
        }

        // Puts the bones of a kind's chains back to their rest pose.
        private void RestChains(ClothChainKind kind)
        {
            var bones = BonesByName();
            foreach (var chain in ClothChains.All)
            {
                if (chain.Kind != kind)
                {
                    continue;
                }

                foreach (var name in chain.Bones)
                {
                    if (bones.TryGetValue(name, out var bone) && rests.TryGetValue(bone, out var rest))
                    {
                        bone.localRotation = rest.Rotation;
                    }
                }
            }
        }

        // Describes the body and its active chains to its world, or takes it out of the world when
        // no chain is active.
        private void Describe()
        {
            var bones = BonesByName();
            laid.Clear();
            foreach (var chain in ClothChains.All)
            {
                if (profiles[(int)chain.Kind].HasValue && HasBones(chain, bones))
                {
                    laid.Add(chain);
                }
            }

            laid.Sort((x, y) => x.Kind != y.Kind ? x.Kind.CompareTo(y.Kind) : RingOrder(x).CompareTo(RingOrder(y)));
            if (laid.Count == 0)
            {
                if (world != null && handle >= 0)
                {
                    world.Remove(handle);
                }

                handle = -1;
                return;
            }

            if (!isActiveAndEnabled)
            {
                return;
            }

            if (shape == null)
            {
                shape = ShapeOf(bones);
                capsules.Clear();
                foreach (var capsuleShape in shape.Capsules)
                {
                    capsules.Add(new Capsule(bones[capsuleShape.Bone], capsuleShape));
                }
            }

            var description = Description(bones);
            if (world == null)
            {
                world = Application.isPlaying ? ClothChainScheduler.World : (ownWorld = new ClothWorld(Layout, 1));
            }

            if (handle < 0)
            {
                handle = world.Add(description);
            }
            else
            {
                world.Replace(handle, description);
            }
        }

        private static bool HasBones(ClothChain chain, Dictionary<string, Transform> bones)
        {
            if (!bones.ContainsKey(chain.Parent))
            {
                return false;
            }

            foreach (var bone in chain.Bones)
            {
                if (!bones.ContainsKey(bone))
                {
                    return false;
                }
            }

            return true;
        }

        // The body as its world takes it: its bones by slot with their bind pose in the body's own
        // space, its legs, its capsules, and its active chains in the order they are laid.
        private ClothWorld.BodyDescription Description(Dictionary<string, Transform> bones)
        {
            var description = new ClothWorld.BodyDescription
            {
                Inputs = new Transform[SlotBones.Length],
                BindMatrices = new float4x4[SlotBones.Length],
                RootSlot = 0,
            };
            var slotOf = new Dictionary<string, int>();
            for (var slot = 0; slot < SlotBones.Length; slot++)
            {
                var name = SlotBones[slot];
                description.Inputs[slot] = name == null ? transform : bones.TryGetValue(name, out var bone) ? bone : null;
                description.BindMatrices[slot] = name != null && shape.BindPose.TryGetValue(name, out var bind)
                    ? (float4x4)bind
                    : float4x4.identity;
                if (name != null)
                {
                    slotOf[name] = slot;
                }
            }

            description.HipsSlot = slotOf["Hips"];
            if (shape.Legs != null)
            {
                description.HasLegs = true;
                description.LegPartSlots = new[] { slotOf["UpperLeg_L"], slotOf["LowerLeg_L"], slotOf["UpperLeg_R"], slotOf["LowerLeg_R"] };
                description.LegEndSlots = new[] { slotOf["LowerLeg_L"], slotOf["Foot_L"], slotOf["LowerLeg_R"], slotOf["Foot_R"] };
                for (var part = 0; part < 4; part++)
                {
                    description.LegsRestInHips[part] = shape.Legs.RestInHips[part];
                }

                description.ForwardInHips = shape.Legs.ForwardInHips;
                description.RightInHips = shape.Legs.RightInHips;
            }

            var head = -1;
            description.Capsules = new ClothWorld.CapsuleDescription[shape.Capsules.Count];
            for (var index = 0; index < shape.Capsules.Count; index++)
            {
                var capsule = shape.Capsules[index];
                description.Capsules[index] = new ClothWorld.CapsuleDescription
                {
                    Slot = slotOf[capsule.Bone],
                    A = capsule.A,
                    B = capsule.B,
                    Front = capsule.Front,
                    Side = capsule.Side,
                    Radii = capsule.Radii,
                    Role = Capsule.RoleOf(capsule.Bone),
                };
                if (capsule.Bone == "Head")
                {
                    head = index;
                }
            }

            description.Chains = new ClothWorld.ChainDescription[laid.Count];
            for (var index = 0; index < laid.Count; index++)
            {
                var chain = laid[index];
                var profile = profiles[(int)chain.Kind].GetValueOrDefault();
                var (left, right) = Neighbours(index);
                var reach = reaches[(int)chain.Kind] != null && reaches[(int)chain.Kind].TryGetValue(chain.Bones[0], out var found) ? found : null;
                var chainBones = new ClothWorld.BoneDescription[chain.Bones.Count];
                for (var b = 0; b < chainBones.Length; b++)
                {
                    var bone = bones[chain.Bones[b]];
                    var rest = RestOf(bone);
                    var next = b + 1 < chainBones.Length ? RestOf(bones[chain.Bones[b + 1]]).Position : (Vector3?)null;
                    chainBones[b] = new ClothWorld.BoneDescription
                    {
                        Transform = bone,
                        LocalPosition = rest.Position,
                        LocalRotation = rest.Rotation,
                        LocalScale = rest.Scale,
                        // The chains hang straight in the rest pose, so the tip bone points like the
                        // one before it, as far.
                        ChildOffset = next ?? (b > 0 ? (Vector3)chainBones[b - 1].ChildOffset : Vector3.down * 0.1f),
                        Beside = b < chain.Beside.Count ? (ClothLegPart)chain.Beside[b] : ClothLegPart.None,
                    };
                }

                description.Chains[index] = new ClothWorld.ChainDescription
                {
                    Kind = (ClothKind)chain.Kind,
                    Angle = chain.Angle,
                    ParentSlot = slotOf[chain.Parent],
                    FrameCapsule = chain.Kind == ClothChainKind.Hood ? head : -1,
                    Left = left,
                    Right = right,
                    Margin = profile.Margin,
                    Slack = profile.Slack,
                    Spring = profile.Spring,
                    Collides = CollidesWith(chain.Kind),
                    Bones = chainBones,
                    Particles = reach != null ? ClothParticles(reach) : null,
                };
            }

            return description;
        }

        // What the chains of a kind collide with: a skirt hangs over the trunk and the legs, a cloak
        // over the arms too, and a hood around the head.
        internal static ClothRoles CollidesWith(ClothChainKind kind)
        {
            switch (kind)
            {
                case ClothChainKind.Cloak:
                    return ClothRoles.Trunk | ClothRoles.Leg | ClothRoles.Arm;
                case ClothChainKind.Hood:
                    return ClothRoles.Trunk;
                default:
                    return ClothRoles.Trunk | ClothRoles.Leg;
            }
        }

        // A chain's neighbours in the laid chains: the skirt's ring around, the cloak's sheet open.
        private (int Left, int Right) Neighbours(int index)
        {
            var kind = laid[index].Kind;
            var first = laid.FindIndex(chain => chain.Kind == kind);
            var count = laid.FindAll(chain => chain.Kind == kind).Count;
            var place = index - first;
            if (count < 2 || kind == ClothChainKind.Hood)
            {
                return (-1, -1);
            }

            if (kind == ClothChainKind.Skirt)
            {
                return (first + (place - 1 + count) % count, first + (place + 1) % count);
            }

            return (place > 0 ? index - 1 : -1, place + 1 < count ? index + 1 : -1);
        }

        // A garment's reach about a chain's particles, in the garment model's meters: a scaled body
        // scales them as it scales everything else.
        private static ClothParticle[] ClothParticles(Vector2[] reach)
        {
            var found = new ClothParticle[reach.Length];
            for (var index = 0; index < reach.Length; index++)
            {
                var has = !float.IsNaN(reach[index].x) && !float.IsNaN(reach[index].y);
                found[index] = new ClothParticle
                {
                    Beyond = has ? reach[index].x : 0f,
                    Within = has ? reach[index].y : 0f,
                    HasCloth = has,
                };
            }

            return found;
        }

        // A chain bone's rest pose, from when the body first described it, before it ever moved.
        private (Vector3 Position, Quaternion Rotation, Vector3 Scale) RestOf(Transform bone)
        {
            if (!rests.TryGetValue(bone, out var rest))
            {
                rest = (bone.localPosition, bone.localRotation, bone.localScale);
                rests[bone] = rest;
            }

            return rest;
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
            return Share(values, 0.5f);
        }

        // The value that a share of the values lie at or below.
        private static float Share(List<float> values, float share)
        {
            if (values.Count == 0)
            {
                return FallbackRadius;
            }

            values.Sort();
            return values[Mathf.Min(values.Count - 1, (int)(values.Count * share))];
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
            private readonly ClothRoles role;
            private readonly List<float> distances = new List<float>();
            private readonly List<float>[] directions = new List<float>[Capsule.Directions];
            private readonly List<Vector3> points = new List<Vector3>();

            public CapsuleMeasure(Vector3 end, Vector3 bodyFront, Vector3 bodyRight, ClothRoles role)
            {
                this.end = end;
                this.role = role;
                var axis = end.sqrMagnitude > 1e-12f ? end.normalized : Vector3.zero;
                front = (bodyFront - axis * Vector3.Dot(bodyFront, axis)).normalized;
                side = (bodyRight - axis * Vector3.Dot(bodyRight, axis) - front * Vector3.Dot(bodyRight, front)).normalized;
                for (var index = 0; index < directions.Length; index++)
                {
                    directions[index] = new List<float>();
                }
            }

            private bool IsSphere => end == Vector3.zero;

            // A leg or an arm is about round and moves under the cloth, which slides around it.
            private bool Round => role != ClothRoles.Trunk;

            public void Add(Vector3 local)
            {
                if (IsSphere)
                {
                    points.Add(local);
                    return;
                }

                var away = local - Capsule.Closest(local, Vector3.zero, end);
                var distance = away.magnitude;
                if (role == ClothRoles.Leg)
                {
                    distances.Add(distance);
                    return;
                }

                // Only the vertices along the segment, so the buttocks below the hips' bone do not
                // widen its capsule at the small of the back, nor the shoulder an upper arm's.
                var along = Vector3.Dot(local, end) / end.sqrMagnitude;
                if (along <= 0f || along >= 1f)
                {
                    return;
                }

                distances.Add(distance);
                if (!Round)
                {
                    directions[Capsule.DirectionOf(away, front, side)].Add(distance);
                }
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

                if (Round)
                {
                    var radius = Share(distances, LimbShare);
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
        // the bind pose of its bones, its capsules, and its legs. Every body of one model measures
        // the same.
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

            public static BodyShape Measure(Transform body, IReadOnlyList<SkinnedMeshRenderer> parts, Dictionary<string, Transform> bones)
            {
                var bindPose = BindPoseOf(body, parts);
                return new BodyShape(bindPose, MeasureCapsules(body, parts, bones, bindPose), LegsShape.Measure(bindPose));
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
                        Capsule.RoleOf(boneName));
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
            private static readonly (string Bone, string End)[] Parts =
            {
                ("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"), ("LowerLeg_R", "Foot_R"),
            };

            public LegsShape(Vector3[] restInHips, Vector3 forwardInHips, Vector3 rightInHips)
            {
                RestInHips = restInHips;
                ForwardInHips = forwardInHips;
                RightInHips = rightInHips;
            }

            public Vector3[] RestInHips { get; }

            public Vector3 ForwardInHips { get; }

            public Vector3 RightInHips { get; }

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

        // A capsule around a body bone as measured from the body: its segment in the bone's own space,
        // and the body's distance from it in each of Directions directions around it, from the
        // body's front toward its right, or one radius for a sphere or a limb, placed where its bone
        // is when asked.
        public sealed class Capsule
        {
            public const int Directions = 16;

            private readonly Transform bone;
            private readonly CapsuleShape shape;

            internal Capsule(Transform bone, CapsuleShape shape)
            {
                this.bone = bone;
                this.shape = shape;
            }

            public string BoneName => shape.Bone;

            // What the capsule is around: the trunk carries the chains, and the limbs move under the
            // cloth.
            public ClothRoles Role => RoleOf(shape.Bone);

            public Vector3 A => bone.TransformPoint(shape.A);

            public Vector3 B => bone.TransformPoint(shape.B);

            // The middle distance of the body from the bone, over every direction.
            public float Radius => shape.Radius * LargestScale(bone.lossyScale);

            public IReadOnlyList<float> Radii => shape.Radii;

            public static ClothRoles RoleOf(string boneName)
            {
                if (boneName.StartsWith("UpperLeg", StringComparison.Ordinal) || boneName.StartsWith("LowerLeg", StringComparison.Ordinal)
                    || boneName.StartsWith("Foot", StringComparison.Ordinal))
                {
                    return ClothRoles.Leg;
                }

                return boneName.StartsWith("UpperArm", StringComparison.Ordinal) || boneName.StartsWith("LowerArm", StringComparison.Ordinal)
                    ? ClothRoles.Arm
                    : ClothRoles.Trunk;
            }

            // The radius toward a direction from the capsule's axis where its bone is, blended between
            // the two directions around it that were measured.
            public float RadiusToward(Vector3 away)
            {
                var radii = shape.Radii;
                var count = radii.Length;
                var scale = LargestScale(bone.lossyScale);
                if (count == 1)
                {
                    return radii[0] * scale;
                }

                var front = bone.TransformDirection(shape.Front);
                var side = bone.TransformDirection(shape.Side);
                var angle = Mathf.Atan2(Vector3.Dot(away, side), Vector3.Dot(away, front));
                var position = (angle + Mathf.PI) / (2f * Mathf.PI) * count - 0.5f;
                var lower = Mathf.FloorToInt(position);
                var share = position - lower;
                return (radii[((lower % count) + count) % count] * (1f - share) + radii[(((lower + 1) % count) + count) % count] * share) * scale;
            }

            // The direction around a bone that an offset from it lies in, from the front toward the
            // side.
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
        }
    }
}
