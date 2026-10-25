using System;
using Mmorpg.Client.Cloth;
using Unity.Mathematics;

namespace Mmorpg.Client.Gameplay
{
    // How the cloth on a set of chains behaves, in SI units: how far it keeps from the body and how
    // far a leg moves under it before it pushes the cloth along, which the target pose reads, and
    // how its springs swing on the target pose, with curves from the chain's root to its tip.
    // Garments name a profile. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    [Serializable]
    public struct ClothProfile
    {
        // A robe, skirt, or coat tail: it hangs close to the legs and swings with every step, and
        // leaves a breathing body's legs room under it.
        public static readonly ClothProfile Robe = new ClothProfile
        {
            Margin = 0.012f,
            Slack = 0.1f,
            FrequencyRoot = 4f,
            FrequencyTip = 2.5f,
            DampingRoot = 0.6f,
            DampingTip = 0.4f,
            ConeRoot = 35f,
            ConeTip = 60f,
            Twist = 18f,
            Gravity = 1f,
            SpeedLean = 0.6f,
            SpeedLeanLimit = 4f,
        };

        // A cloak: it lags behind turns and sways, as stiff at its hem as at its top, so it stays
        // taut rather than flick, as Victor asked on 2026-10-25, and the wind of its speed leans it
        // back from the heels.
        public static readonly ClothProfile Cloak = new ClothProfile
        {
            Margin = 0.02f,
            Slack = 0.05f,
            FrequencyRoot = 2.5f,
            FrequencyTip = 2.5f,
            DampingRoot = 0.6f,
            DampingTip = 0.6f,
            ConeRoot = 40f,
            ConeTip = 55f,
            Twist = 9f,
            Gravity = 1f,
            SpeedLean = 8f,
            SpeedLeanLimit = 30f,
        };

        // A hood: stiff, it only gives a little.
        public static readonly ClothProfile Hood = new ClothProfile
        {
            Margin = 0.01f,
            Slack = 0f,
            FrequencyRoot = 6f,
            FrequencyTip = 6f,
            DampingRoot = 0.7f,
            DampingTip = 0.7f,
            ConeRoot = 20f,
            ConeTip = 20f,
            Twist = 0f,
            Gravity = 1f,
            SpeedLean = 0.3f,
            SpeedLeanLimit = 2f,
        };

        // How far the cloth keeps from the body, in meters.
        public float Margin;

        // How far a leg turns the cloth's hanging direction outward before the cloth follows it, in
        // the outward share of a unit direction: 0.1 is about 6 degrees of a leg's turn.
        public float Slack;

        // How fast the springs pull the cloth back to its target, in Hz, at the chain's root and tip.
        public float FrequencyRoot;
        public float FrequencyTip;

        // How much of a swing dies down, as a damping ratio: 1 swings back without passing the target.
        public float DampingRoot;
        public float DampingTip;

        // How far a bone may turn from where the target points it, in degrees, at the root and the tip.
        public float ConeRoot;
        public float ConeTip;

        // How far the chains may twist around the anchor's upright axis, in degrees; 0 for no limit.
        public float Twist;

        // The share of gravity the cloth feels where the anchor leans away from the bind pose.
        public float Gravity;

        // How far the anchor's speed leans the cloth back, as a wind, in meters a second squared per
        // meter a second, and at most; it bends the cloth the more the farther it hangs from its root.
        public float SpeedLean;
        public float SpeedLeanLimit;

        // The springs as the cloth world takes them.
        public ClothSpring Spring => new ClothSpring
        {
            FrequencyRoot = FrequencyRoot,
            FrequencyTip = FrequencyTip,
            DampingRoot = DampingRoot,
            DampingTip = DampingTip,
            ConeRoot = math.radians(ConeRoot),
            ConeTip = math.radians(ConeTip),
            Twist = math.radians(Twist),
            Gravity = Gravity,
            SpeedLean = SpeedLean,
            SpeedLeanLimit = SpeedLeanLimit,
        };

        public static ClothProfile For(ClothChainKind kind)
        {
            switch (kind)
            {
                case ClothChainKind.Cloak:
                    return Cloak;
                case ClothChainKind.Hood:
                    return Hood;
                default:
                    return Robe;
            }
        }
    }
}
