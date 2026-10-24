using System;

namespace Mmorpg.Client.Gameplay
{
    // How the cloth on a set of chains behaves: how strongly it springs back to hanging from the
    // body as animated, how fast its swing dies down, how much it weighs, how far it keeps from
    // the body, and how far a leg moves under it before it pushes the cloth along. Garments name a
    // profile; the values are given per 1/60 s and the solver scales them to the frame time. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    [Serializable]
    public struct ClothProfile
    {
        // A robe, skirt, or coat tail: it hangs close to the legs and swings with every step, and
        // leaves a breathing body's legs room under it.
        public static readonly ClothProfile Robe = new ClothProfile(0.12f, 0.10f, 1f, 0.012f, 0.1f);

        // A cloak: lighter, it lags behind turns and sways longer, and a leg that kicks back pushes it
        // along soon.
        public static readonly ClothProfile Cloak = new ClothProfile(0.06f, 0.06f, 1f, 0.02f, 0.05f);

        // A hood: stiff, it only gives a little.
        public static readonly ClothProfile Hood = new ClothProfile(0.45f, 0.20f, 1f, 0.01f, 0f);

        public float Stiffness;
        public float Damping;
        public float Gravity;
        public float Margin;

        // How far a leg turns the cloth's hanging direction outward before the cloth follows it, in
        // the outward share of a unit direction: 0.1 is about 6 degrees of a leg's turn.
        public float Slack;

        public ClothProfile(float stiffness, float damping, float gravity, float margin, float slack)
        {
            Stiffness = stiffness;
            Damping = damping;
            Gravity = gravity;
            Margin = margin;
            Slack = slack;
        }

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
