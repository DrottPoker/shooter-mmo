using System;

namespace Mmorpg.Client.Gameplay
{
    // How the cloth on a set of chains behaves: how strongly it springs back to hanging from the
    // body as animated, how fast its swing dies down, how much it weighs, and how far it keeps from
    // the body. Garments name a profile; the values are given per 1/60 s and the solver scales them
    // to the frame time. See docs/EQUIPMENT_VISUALS_DESIGN.md.
    [Serializable]
    public struct ClothProfile
    {
        // A robe, skirt, or coat tail: it hangs close to the legs and swings with every step.
        public static readonly ClothProfile Robe = new ClothProfile(0.12f, 0.10f, 1f, 0.012f);

        // A cloak: lighter, it lags behind turns and sways longer.
        public static readonly ClothProfile Cloak = new ClothProfile(0.06f, 0.06f, 1f, 0.02f);

        // A hood: stiff, it only gives a little.
        public static readonly ClothProfile Hood = new ClothProfile(0.45f, 0.20f, 1f, 0.01f);

        public float Stiffness;
        public float Damping;
        public float Gravity;
        public float Margin;

        public ClothProfile(float stiffness, float damping, float gravity, float margin)
        {
            Stiffness = stiffness;
            Damping = damping;
            Gravity = gravity;
            Margin = margin;
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
