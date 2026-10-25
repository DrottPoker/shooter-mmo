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

    // How a chain swings on its target pose, in SI units, from its root to its tip: how fast its
    // particles spring back to the target, in Hz, and how much of their swing dies down, as a
    // damping ratio; how far each bone may turn from where the target points it, and the chain
    // around its anchor's upright axis, in radians, with no twist limit at zero; the share of
    // gravity it feels against the bind pose; and how far the anchor's speed leans it back, in
    // meters a second squared per meter a second, and at most.
    public struct ClothSpring
    {
        public float FrequencyRoot;
        public float FrequencyTip;
        public float DampingRoot;
        public float DampingTip;
        public float ConeRoot;
        public float ConeTip;
        public float Twist;
        public float Gravity;
        public float SpeedLean;
        public float SpeedLeanLimit;
    }

    // A chain's springs and the frame they run in: the slot and the kind of its anchor bone, and the
    // body's upright axis in the anchor's own frame in the bind pose.
    public struct ClothSpringChain
    {
        public ClothSpring Spring;
        public int AnchorSlot;
        public int Anchor;
        public float3 Up;
    }

    // How an anchor bone moves in the world as the springs feel it: where it was last frame, its
    // speed and turning filtered, and what the springs feel of them, limited, this frame and the
    // last, which the steps between them take in between.
    public struct ClothAnchor
    {
        public float3 Position;
        public quaternion Rotation;
        public float3 Velocity;
        public float3 AngularVelocity;
        public float3 FeltVelocity;
        public float3 FeltAcceleration;
        public float3 FeltAngularVelocity;
        public float3 FeltAngularAcceleration;
        public float3 LastFeltVelocity;
        public float3 LastFeltAcceleration;
        public float3 LastFeltAngularVelocity;
        public float3 LastFeltAngularAcceleration;

        // 0 before the first frame, 1 after it, and 2 once it moved.
        public byte Primed;
    }

    // A body's springs: whether they run and from where, how far they are faded in, the time not
    // yet stepped, whether the body jumped, and whether its cloth sleeps on its target and for how
    // long it lay within reach of it.
    public struct ClothSpringBody
    {
        public bool Started;
        public bool Jumped;
        public bool Sleeping;
        public float Weight;
        public float Accumulator;
        public float SleepTime;
    }
}
