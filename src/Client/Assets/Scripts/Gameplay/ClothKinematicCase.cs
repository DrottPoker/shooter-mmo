using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The kinematic fixtures: bodies captured in poses with what ClothKinematics lays from them,
    // written by MMORPG > Tools > Art Pipeline > Write Cloth Fixtures to
    // tools/blender/tests/fixtures/cloth_kinematics.json. The Unity tests and the art pipeline's
    // Python tests lay every case again and compare, so the solver and cloth_core.py stay alike.
    // Plain fields, as JsonUtility writes them; no infinities or NaN, which JSON cannot hold.
    [Serializable]
    public sealed class ClothKinematicFixture
    {
        public const int Version = 1;

        public int version = Version;
        public List<ClothKinematicCase> cases = new List<ClothKinematicCase>();
    }

    // One body in one pose: its capsules and legs as posed, and its active chains as they hang
    // from their animated parents at rest, in world space.
    [Serializable]
    public sealed class ClothKinematicCase
    {
        public string name;
        public float scale = 1f;
        public Vector3 up = Vector3.up;
        public bool hasLegs;
        public Vector3 hips;
        public Vector3 forward;
        public Vector3 right;
        public float ahead;
        public Quaternion[] legTurns = new Quaternion[0];
        public List<ClothKinematicCapsule> capsules = new List<ClothKinematicCapsule>();
        public List<ClothKinematicChain> chains = new List<ClothKinematicChain>();

        public bool HasLegs => hasLegs && legTurns.Length == 4;

        public ClothKinematics.LegPose Legs()
        {
            var legs = new ClothKinematics.LegPose
            {
                Forward = forward,
                Right = right,
                Hips = hips,
                Ahead = ahead,
            };
            for (var index = 0; index < legs.Turns.Length; index++)
            {
                legs.Turns[index] = legTurns[index];
            }

            return legs;
        }
    }

    // A capsule as posed: its segment, the body's front and side around it, its radii from the
    // front toward the side, how much it is scaled, and the way out of a point on its axis.
    [Serializable]
    public sealed class ClothKinematicCapsule
    {
        public string bone;
        public Vector3 a;
        public Vector3 b;
        public Vector3 front;
        public Vector3 side;
        public Vector3 axisNormal;
        public float scale = 1f;
        public float[] radii = new float[0];

        public ClothChainSolver.Capsule Posed()
        {
            return ClothChainSolver.Capsule.Posed(bone, radii, a, b, front, side, scale, axisNormal);
        }
    }

    // A chain: its kind, its angle around the hips, the parts of the legs its bones lie beside, its
    // profile's margin and slack, where it hangs at rest from its posed parent, its bones' lengths,
    // how deep each particle lies in each capsule in the bind pose (restDepthKnown false for no
    // limit), how far the garment's cloth reaches about each particle (reachKnown false without the
    // garment, hasReach false where a particle has no cloth), and what ClothKinematics laid.
    [Serializable]
    public sealed class ClothKinematicChain
    {
        public string root;
        public string kind;
        public float angle;
        public string[] beside = new string[0];
        public float margin;
        public float slack;
        public Vector3[] hanging = new Vector3[0];
        public float[] lengths = new float[0];
        public float[] restDepths = new float[0];
        public bool[] restDepthKnown = new bool[0];
        public bool reachKnown;
        public bool[] hasReach = new bool[0];
        public Vector2[] reach = new Vector2[0];
        public Vector3[] expected = new Vector3[0];

        public ClothChainKind Kind => (ClothChainKind)Enum.Parse(typeof(ClothChainKind), kind, true);

        public LegPart[] Beside
        {
            get
            {
                var parts = new LegPart[beside.Length];
                for (var index = 0; index < parts.Length; index++)
                {
                    parts[index] = (LegPart)Enum.Parse(typeof(LegPart), beside[index], true);
                }

                return parts;
            }
        }

        public bool HasAnyReach => reachKnown;

        public Vector2[] Reach()
        {
            if (!reachKnown)
            {
                return null;
            }

            var found = new Vector2[reach.Length];
            for (var index = 0; index < found.Length; index++)
            {
                found[index] = hasReach[index] ? reach[index] : new Vector2(float.NaN, float.NaN);
            }

            return found;
        }

        public float[] RestDepths()
        {
            if (restDepths.Length == 0)
            {
                return null;
            }

            var found = new float[restDepths.Length];
            for (var index = 0; index < found.Length; index++)
            {
                found[index] = restDepthKnown[index] ? restDepths[index] : float.NegativeInfinity;
            }

            return found;
        }

        // The chain's values as a case holds them, with what it laid.
        public static ClothKinematicChain Of(ClothChain chain, ClothProfile profile, Vector3[] hanging, float[] lengths,
            float[] restDepths, Vector2[] reach, Vector3[] laid)
        {
            var captured = new ClothKinematicChain
            {
                root = chain.Bones[0],
                kind = chain.Kind.ToString().ToLowerInvariant(),
                angle = chain.Angle,
                beside = new string[chain.Beside.Count],
                margin = profile.Margin,
                slack = profile.Slack,
                hanging = (Vector3[])hanging.Clone(),
                lengths = (float[])lengths.Clone(),
                reachKnown = reach != null,
                expected = laid != null ? (Vector3[])laid.Clone() : new Vector3[0],
            };
            for (var index = 0; index < captured.beside.Length; index++)
            {
                captured.beside[index] = chain.Beside[index].ToString().ToLowerInvariant();
            }

            if (restDepths != null)
            {
                captured.restDepths = new float[restDepths.Length];
                captured.restDepthKnown = new bool[restDepths.Length];
                for (var index = 0; index < restDepths.Length; index++)
                {
                    captured.restDepthKnown[index] = !float.IsInfinity(restDepths[index]) && !float.IsNaN(restDepths[index]);
                    captured.restDepths[index] = captured.restDepthKnown[index] ? restDepths[index] : 0f;
                }
            }

            if (reach != null)
            {
                captured.reach = new Vector2[reach.Length];
                captured.hasReach = new bool[reach.Length];
                for (var index = 0; index < reach.Length; index++)
                {
                    captured.hasReach[index] = !float.IsNaN(reach[index].x) && !float.IsNaN(reach[index].y);
                    captured.reach[index] = captured.hasReach[index] ? reach[index] : Vector2.zero;
                }
            }

            return captured;
        }
    }
}
