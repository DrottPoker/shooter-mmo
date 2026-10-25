using System;
using Unity.Mathematics;

namespace Mmorpg.Client.Cloth
{
    // The kinds of cloth chains, in the order their cloth lies on the body, so the cloth under
    // another is laid before it.
    public enum ClothKind : byte
    {
        // Eight chains around the hips that hang to the ankles: skirts, robes, coat tails.
        Skirt,

        // Five chains down the back from the shoulder blades to the middle of the calves: cloaks.
        Cloak,

        // One chain behind the head: the back and tip of a hood.
        Hood,
    }

    // The part of the legs a chain bone hangs beside, which it follows.
    public enum ClothLegPart : byte
    {
        None,
        Thigh,
        Shin,
    }

    // What a capsule is around: the trunk carries the chains, and the legs and arms move under the
    // cloth.
    [Flags]
    public enum ClothRoles : byte
    {
        None = 0,
        Trunk = 1,
        Leg = 2,
        Arm = 4,
    }

    // A capsule where it is posed: its segment, the body's front and side around it, how much it
    // is scaled, the way out of a point on its axis, its radii from the front toward the side in
    // the world's radii, and what it is around.
    public struct ClothCapsule
    {
        public float3 A;
        public float3 B;
        public float3 Front;
        public float3 Side;
        public float3 AxisNormal;
        public float Scale;
        public float Widest;
        public int RadiiStart;
        public int RadiiCount;
        public ClothRoles Role;
    }

    // Where a body's legs are: how each thigh and shin turns against the posed hips from where it
    // points at rest, the hips' forward and right axes, and where the hips are.
    public struct ClothLegs
    {
        public quaternion ThighL;
        public quaternion ShinL;
        public quaternion ThighR;
        public quaternion ShinR;
        public float3 Forward;
        public float3 Right;
        public float3 Hips;
        public bool Valid;
    }

    // A chain of a body, in the order the body's chains are laid: its kind, its angle around the
    // hips in degrees from the front toward the right, its bones and particles, its neighbours in
    // the body's chains (-1 for none), its profile's margin and slack, whether its garment's reach
    // is known, and what it collides with.
    public struct ClothChainData
    {
        public ClothKind Kind;
        public float Angle;
        public int BoneStart;
        public int BoneCount;
        public int ParticleStart;
        public int Left;
        public int Right;
        public float Margin;
        public float Slack;
        public bool ReachKnown;
        public ClothRoles Collides;
    }

    // A chain bone: the part of the legs it hangs beside.
    public struct ClothBone
    {
        public ClothLegPart Beside;
    }

    // A chain's particle, a joint or its tip: how far the garment's cloth reaches beyond it, the
    // way the chain faces, and within it, and whether there is cloth at it.
    public struct ClothParticle
    {
        public float Beyond;
        public float Within;
        public bool HasCloth;
    }
}
