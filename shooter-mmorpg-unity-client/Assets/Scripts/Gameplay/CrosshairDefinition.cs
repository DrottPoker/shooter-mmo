using UnityEngine;

namespace ShooterMmo.Gameplay
{
    public enum CrosshairShape
    {
        Dot,
        Cross
    }

    public readonly struct CrosshairDefinition
    {
        public CrosshairDefinition(
            CrosshairShape shape,
            float size,
            float thickness,
            float gap,
            Color color)
        {
            Shape = shape;
            Size = Mathf.Max(1f, size);
            Thickness = Mathf.Clamp(thickness, 1f, Size);
            Gap = Mathf.Max(0f, gap);
            Color = color;
        }

        public CrosshairShape Shape { get; }

        public float Size { get; }

        public float Thickness { get; }

        public float Gap { get; }

        public Color Color { get; }

        public static CrosshairDefinition Unarmed
        {
            get { return new CrosshairDefinition(CrosshairShape.Dot, 4f, 2f, 0f, Color.white); }
        }
    }
}
