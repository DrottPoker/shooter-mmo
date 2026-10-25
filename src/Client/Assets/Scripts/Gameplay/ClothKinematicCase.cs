using System;
using System.Collections.Generic;
using Mmorpg.Client.Cloth;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The kinematic fixtures: bodies captured in poses with what ClothPose lays from them, written by
    // MMORPG > Tools > Art Pipeline > Write Cloth Fixtures to
    // tools/blender/tests/fixtures/cloth_kinematics.json. The Unity tests and the art pipeline's
    // Python tests lay every case again and compare, so the client and cloth_core.py stay alike.
    // Plain fields, as JsonUtility writes them; no infinities or NaN, which JSON cannot hold.
    [Serializable]
    public sealed class ClothKinematicFixture
    {
        public const int Version = 2;

        public int version = Version;
        public List<ClothKinematicCase> cases = new List<ClothKinematicCase>();
    }

    // One body in one pose: its capsules as posed and in the bind pose, its legs, and its active
    // chains in the order they are laid, as they hang from their posed parents at rest and in the
    // bind pose, in world space.
    [Serializable]
    public sealed class ClothKinematicCase
    {
        public string name;
        public float scale = 1f;
        public bool hasLegs;
        public Vector3 hips;
        public Vector3 forward;
        public Vector3 right;
        public Quaternion[] legTurns = new Quaternion[0];
        public List<ClothKinematicCapsule> capsules = new List<ClothKinematicCapsule>();
        public List<ClothKinematicCapsule> bindCapsules = new List<ClothKinematicCapsule>();
        public List<ClothKinematicChain> chains = new List<ClothKinematicChain>();

        public ClothLegs Legs()
        {
            return hasLegs && legTurns.Length == 4
                ? new ClothLegs
                {
                    ThighL = legTurns[0],
                    ShinL = legTurns[1],
                    ThighR = legTurns[2],
                    ShinR = legTurns[3],
                    Forward = forward,
                    Right = right,
                    Hips = hips,
                    Valid = true,
                }
                : new ClothLegs();
        }

        // A body as a world last laid it, with what it laid.
        public static ClothKinematicCase Of(string name, ClothSnapshot snapshot, IReadOnlyList<ClothChainSolver.Capsule> bodyCapsules,
            IReadOnlyList<ClothChain> laid)
        {
            var captured = new ClothKinematicCase
            {
                name = name,
                scale = snapshot.Scale,
                hasLegs = snapshot.Legs.Valid,
                hips = snapshot.Legs.Hips,
                forward = snapshot.Legs.Forward,
                right = snapshot.Legs.Right,
                legTurns = snapshot.Legs.Valid
                    ? new Quaternion[] { snapshot.Legs.ThighL, snapshot.Legs.ShinL, snapshot.Legs.ThighR, snapshot.Legs.ShinR }
                    : new Quaternion[0],
            };
            for (var index = 0; index < snapshot.Capsules.Length; index++)
            {
                var bone = index < bodyCapsules.Count ? bodyCapsules[index].BoneName : string.Empty;
                captured.capsules.Add(ClothKinematicCapsule.Of(bone, snapshot.Capsules[index], snapshot.Radii[index]));
                captured.bindCapsules.Add(ClothKinematicCapsule.Of(bone, snapshot.BindCapsules[index], snapshot.Radii[index]));
            }

            for (var index = 0; index < snapshot.Chains.Length; index++)
            {
                captured.chains.Add(ClothKinematicChain.Of(index < laid.Count ? laid[index].Bones[0] : string.Empty, snapshot, index));
            }

            return captured;
        }

        // Lays the case as ClothPose lays a body: its margins from its bind pose, then its chains.
        // Returns each chain's particles, in the order of the case.
        public Vector3[][] Lay()
        {
            var capsuleCount = capsules.Count;
            var boneCount = 0;
            var particleCount = 0;
            var radiusCount = 0;
            foreach (var chain in chains)
            {
                boneCount += chain.beside.Length;
                particleCount += chain.beside.Length + 1;
            }

            foreach (var capsule in capsules)
            {
                radiusCount += capsule.radii.Length;
            }

            var posed = new NativeArray<ClothCapsule>(capsuleCount, Allocator.Temp);
            var bind = new NativeArray<ClothCapsule>(capsuleCount, Allocator.Temp);
            var radii = new NativeArray<float>(Math.Max(1, radiusCount), Allocator.Temp);
            var laidChains = new NativeArray<ClothChainData>(chains.Count, Allocator.Temp);
            var frames = new NativeArray<int>(chains.Count, Allocator.Temp);
            var bones = new NativeArray<ClothBone>(Math.Max(1, boneCount), Allocator.Temp);
            var particles = new NativeArray<ClothParticle>(Math.Max(1, particleCount), Allocator.Temp);
            var hanging = new NativeArray<float3>(Math.Max(1, particleCount), Allocator.Temp);
            var bindHanging = new NativeArray<float3>(Math.Max(1, particleCount), Allocator.Temp);
            var positions = new NativeArray<float3>(Math.Max(1, particleCount), Allocator.Temp);
            var directions = new NativeArray<float3>(Math.Max(1, boneCount), Allocator.Temp);
            var margins = new NativeArray<float>(Math.Max(1, boneCount * ClothPose.Samples * capsuleCount), Allocator.Temp);
            try
            {
                var radius = 0;
                for (var index = 0; index < capsuleCount; index++)
                {
                    posed[index] = capsules[index].Capsule(radius);
                    bind[index] = bindCapsules[index].Capsule(radius);
                    foreach (var each in capsules[index].radii)
                    {
                        radii[radius++] = each;
                    }
                }

                var bone = 0;
                var particle = 0;
                for (var index = 0; index < chains.Count; index++)
                {
                    var chain = chains[index];
                    laidChains[index] = chain.Chain(bone, particle);
                    frames[index] = chain.frameCapsule;
                    for (var b = 0; b < chain.beside.Length; b++)
                    {
                        bones[bone + b] = new ClothBone { Beside = ClothKinematicChain.PartOf(chain.beside[b]) };
                    }

                    for (var p = 0; p <= chain.beside.Length; p++)
                    {
                        particles[particle + p] = chain.reachKnown && chain.hasReach[p]
                            ? new ClothParticle { Beyond = chain.reach[p].x, Within = chain.reach[p].y, HasCloth = true }
                            : new ClothParticle();
                        hanging[particle + p] = chain.hanging[p];
                        bindHanging[particle + p] = chain.bindHanging[p];
                    }

                    bone += chain.beside.Length;
                    particle += chain.beside.Length + 1;
                }

                ClothPose.Margins(0, chains.Count, 0, 0, capsuleCount, 1f, laidChains, particles, bind, radii, bindHanging, margins, 0);
                ClothPose.Lay(0, chains.Count, 0, 0, capsuleCount, 0, scale, Legs(), frames, laidChains, bones, particles, posed, radii,
                    margins, hanging, positions, directions);
                var found = new Vector3[chains.Count][];
                particle = 0;
                for (var index = 0; index < chains.Count; index++)
                {
                    found[index] = new Vector3[chains[index].beside.Length + 1];
                    for (var p = 0; p < found[index].Length; p++)
                    {
                        found[index][p] = positions[particle + p];
                    }

                    particle += found[index].Length;
                }

                return found;
            }
            finally
            {
                posed.Dispose();
                bind.Dispose();
                radii.Dispose();
                laidChains.Dispose();
                frames.Dispose();
                bones.Dispose();
                particles.Dispose();
                hanging.Dispose();
                bindHanging.Dispose();
                positions.Dispose();
                directions.Dispose();
                margins.Dispose();
            }
        }
    }

    // A capsule as posed: its bone and what it is around, its segment, the body's front and side
    // around it, the way out of a point on its axis, how much it is scaled, and its radii from the
    // front toward the side.
    [Serializable]
    public sealed class ClothKinematicCapsule
    {
        public string bone;
        public string role;
        public Vector3 a;
        public Vector3 b;
        public Vector3 front;
        public Vector3 side;
        public Vector3 axisNormal;
        public float scale = 1f;
        public float[] radii = new float[0];

        public static ClothKinematicCapsule Of(string bone, ClothCapsule capsule, float[] radii)
        {
            return new ClothKinematicCapsule
            {
                bone = bone,
                role = capsule.Role.ToString().ToLowerInvariant(),
                a = capsule.A,
                b = capsule.B,
                front = capsule.Front,
                side = capsule.Side,
                axisNormal = capsule.AxisNormal,
                scale = capsule.Scale,
                radii = (float[])radii.Clone(),
            };
        }

        public ClothCapsule Capsule(int radiiStart)
        {
            var widest = 0f;
            foreach (var each in radii)
            {
                widest = Mathf.Max(widest, each);
            }

            return new ClothCapsule
            {
                A = a,
                B = b,
                Front = front,
                Side = side,
                AxisNormal = axisNormal,
                Scale = scale,
                Widest = widest * scale,
                RadiiStart = radiiStart,
                RadiiCount = radii.Length,
                Role = (ClothRoles)Enum.Parse(typeof(ClothRoles), role, true),
            };
        }
    }

    // A chain: its first bone, kind, angle around the hips, the parts of the legs its bones lie
    // beside, its profile's margin and slack, what it collides with, its neighbours among the case's
    // chains and the capsule of its frame (-1 for none), where it hangs from its posed parent at rest
    // and in the bind pose, how far the garment's cloth reaches about each particle (reachKnown false
    // without the garment, hasReach false where a particle has no cloth; x beyond, y within), and
    // what ClothPose laid.
    [Serializable]
    public sealed class ClothKinematicChain
    {
        public string root;
        public string kind;
        public float angle;
        public string[] beside = new string[0];
        public float margin;
        public float slack;
        public string[] collides = new string[0];
        public int left = -1;
        public int right = -1;
        public int frameCapsule = -1;
        public Vector3[] hanging = new Vector3[0];
        public Vector3[] bindHanging = new Vector3[0];
        public bool reachKnown;
        public bool[] hasReach = new bool[0];
        public Vector2[] reach = new Vector2[0];
        public Vector3[] expected = new Vector3[0];

        public static ClothLegPart PartOf(string name) => (ClothLegPart)Enum.Parse(typeof(ClothLegPart), name, true);

        public static ClothKinematicChain Of(string root, ClothSnapshot snapshot, int index)
        {
            var chain = snapshot.Chains[index];
            var captured = new ClothKinematicChain
            {
                root = root,
                kind = chain.Kind.ToString().ToLowerInvariant(),
                angle = chain.Angle,
                beside = new string[chain.BoneCount],
                margin = chain.Margin,
                slack = chain.Slack,
                left = chain.Left,
                right = chain.Right,
                frameCapsule = snapshot.FrameCapsules[index],
                reachKnown = chain.ReachKnown,
            };
            for (var bone = 0; bone < chain.BoneCount; bone++)
            {
                captured.beside[bone] = snapshot.Beside[index][bone].ToString().ToLowerInvariant();
            }

            var roles = new List<string>();
            foreach (ClothRoles role in new[] { ClothRoles.Trunk, ClothRoles.Leg, ClothRoles.Arm })
            {
                if ((chain.Collides & role) != 0)
                {
                    roles.Add(role.ToString().ToLowerInvariant());
                }
            }

            captured.collides = roles.ToArray();
            var count = chain.BoneCount + 1;
            captured.hanging = new Vector3[count];
            captured.bindHanging = new Vector3[count];
            captured.expected = new Vector3[count];
            captured.hasReach = new bool[chain.ReachKnown ? count : 0];
            captured.reach = new Vector2[chain.ReachKnown ? count : 0];
            for (var particle = 0; particle < count; particle++)
            {
                captured.hanging[particle] = snapshot.Hanging[index][particle];
                captured.bindHanging[particle] = snapshot.BindHanging[index][particle];
                captured.expected[particle] = snapshot.Positions[index][particle];
                if (chain.ReachKnown)
                {
                    var found = snapshot.Particles[index][particle];
                    captured.hasReach[particle] = found.HasCloth;
                    captured.reach[particle] = found.HasCloth ? new Vector2(found.Beyond, found.Within) : Vector2.zero;
                }
            }

            return captured;
        }

        public ClothChainData Chain(int boneStart, int particleStart)
        {
            var collidesWith = ClothRoles.None;
            foreach (var role in collides)
            {
                collidesWith |= (ClothRoles)Enum.Parse(typeof(ClothRoles), role, true);
            }

            return new ClothChainData
            {
                Kind = (ClothKind)Enum.Parse(typeof(ClothKind), kind, true),
                Angle = angle,
                BoneStart = boneStart,
                BoneCount = beside.Length,
                ParticleStart = particleStart,
                Left = left,
                Right = right,
                Margin = margin,
                Slack = slack,
                ReachKnown = reachKnown,
                Collides = collidesWith,
            };
        }
    }
}
