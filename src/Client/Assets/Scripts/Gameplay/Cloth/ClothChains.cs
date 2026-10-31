using System.Collections.Generic;

namespace Mmorpg.Client.Gameplay
{
    public enum ClothChainKind
    {
        // Eight chains around the hips that hang to the ankles: skirts, robes, coat tails.
        Skirt,

        // Five chains down the back from the shoulder blades to the middle of the calves: cloaks and
        // capes.
        Cloak,

        // One chain behind the head: the back and tip of a hood.
        Hood,
    }

    // The part of the legs a bone of a chain hangs beside, which it follows.
    public enum LegPart
    {
        None,
        Thigh,
        Shin,
    }

    // The cloth chains of the Humanoid skeleton, art/Skeletons/Humanoid.json, which loose garments
    // hang on. Each chain lists its bones from the one at its parent to its tip. The body is never
    // weighted to them and Humanoid clips do not move them; ClothChainSolver does. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothChain
    {
        private static readonly LegPart[] NoParts = new LegPart[0];

        public ClothChain(ClothChainKind kind, string parent, string[] bones, float angle = 0f, LegPart[] beside = null)
        {
            Kind = kind;
            Parent = parent;
            Bones = bones;
            Angle = angle;
            Beside = beside ?? NoParts;
        }

        public ClothChainKind Kind { get; }

        // The body bone the chain hangs from.
        public string Parent { get; }

        public IReadOnlyList<string> Bones { get; }

        // The chain's angle around the hips in degrees, from the front toward the right side,
        // negative on the left: the way it faces and the leg it follows.
        public float Angle { get; }

        // The part of the legs each of its bones hangs beside, from its root, as the rig places
        // the chains; empty for a chain that does not follow the legs.
        public IReadOnlyList<LegPart> Beside { get; }
    }

    public static class ClothChains
    {
        public const int SkirtBones = 4;
        public const int CloakBones = 5;
        public const int HoodBones = 2;

        // A skirt hangs from the hips to the ankles, a cloak from the shoulder blades to the middle
        // of the calves. Declared before All, which Build fills from them.
        private static readonly LegPart[] SkirtBeside = { LegPart.Thigh, LegPart.Thigh, LegPart.Shin, LegPart.Shin };
        private static readonly LegPart[] CloakBeside = { LegPart.None, LegPart.None, LegPart.Thigh, LegPart.Thigh, LegPart.Shin };

        public static readonly IReadOnlyList<ClothChain> All = Build();

        private static ClothChain[] Build()
        {
            var chains = new List<ClothChain>();
            foreach (var (name, angle, suffixes) in new[]
            {
                ("SkirtFront", 0f, new[] { string.Empty }),
                ("SkirtFrontSide", 45f, new[] { "_L", "_R" }),
                ("SkirtSide", 90f, new[] { "_L", "_R" }),
                ("SkirtBackSide", 135f, new[] { "_L", "_R" }),
                ("SkirtBack", 180f, new[] { string.Empty }),
            })
            {
                foreach (var suffix in suffixes)
                {
                    chains.Add(Chain(ClothChainKind.Skirt, "Hips", name, SkirtBones, suffix, suffix == "_L" ? -angle : angle,
                        SkirtBeside));
                }
            }

            foreach (var (name, angle, suffixes) in new[]
            {
                ("CloakCenter", 180f, new[] { string.Empty }),
                ("CloakInner", 157.5f, new[] { "_L", "_R" }),
                ("CloakOuter", 135f, new[] { "_L", "_R" }),
            })
            {
                foreach (var suffix in suffixes)
                {
                    chains.Add(Chain(ClothChainKind.Cloak, "UpperChest", name, CloakBones, suffix, suffix == "_L" ? -angle : angle,
                        CloakBeside));
                }
            }

            // The hood's chain hangs behind the head, so its cloth reaches backward from it.
            chains.Add(Chain(ClothChainKind.Hood, "Head", "Hood", HoodBones, string.Empty, 180f));
            return chains.ToArray();
        }

        private static ClothChain Chain(ClothChainKind kind, string parent, string name, int count, string suffix,
            float angle = 0f, LegPart[] beside = null)
        {
            var bones = new string[count];
            for (var index = 0; index < count; index++)
            {
                bones[index] = name + (index + 1) + suffix;
            }

            return new ClothChain(kind, parent, bones, angle, beside);
        }
    }
}
