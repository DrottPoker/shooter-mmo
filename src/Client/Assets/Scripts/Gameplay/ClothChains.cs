using System.Collections.Generic;

namespace Mmorpg.Client.Gameplay
{
    public enum ClothChainKind
    {
        // Eight chains around the hips that hang to the ankles: skirts, robes, coat tails.
        Skirt,

        // Five chains down the back from the shoulder blades to the knees: cloaks and capes.
        Cloak,

        // One chain behind the head: the back and tip of a hood.
        Hood,
    }

    // The cloth chains of the Humanoid skeleton, art/Skeletons/Humanoid.json, which loose garments
    // hang on. Each chain lists its bones from the one at its parent to its tip. The body is never
    // weighted to them and Humanoid clips do not move them; ClothChainSolver does. See
    // docs/EQUIPMENT_VISUALS_DESIGN.md.
    public sealed class ClothChain
    {
        public ClothChain(ClothChainKind kind, string parent, string[] bones)
        {
            Kind = kind;
            Parent = parent;
            Bones = bones;
        }

        public ClothChainKind Kind { get; }

        // The body bone the chain hangs from.
        public string Parent { get; }

        public IReadOnlyList<string> Bones { get; }
    }

    public static class ClothChains
    {
        public const int SkirtBones = 4;
        public const int CloakBones = 5;
        public const int HoodBones = 2;

        public static readonly IReadOnlyList<ClothChain> All = Build();

        private static ClothChain[] Build()
        {
            var chains = new List<ClothChain>();
            foreach (var (name, suffixes) in new[]
            {
                ("SkirtFront", new[] { string.Empty }),
                ("SkirtFrontSide", new[] { "_L", "_R" }),
                ("SkirtSide", new[] { "_L", "_R" }),
                ("SkirtBackSide", new[] { "_L", "_R" }),
                ("SkirtBack", new[] { string.Empty }),
            })
            {
                foreach (var suffix in suffixes)
                {
                    chains.Add(Chain(ClothChainKind.Skirt, "Hips", name, SkirtBones, suffix));
                }
            }

            foreach (var (name, suffixes) in new[]
            {
                ("CloakCenter", new[] { string.Empty }),
                ("CloakInner", new[] { "_L", "_R" }),
                ("CloakOuter", new[] { "_L", "_R" }),
            })
            {
                foreach (var suffix in suffixes)
                {
                    chains.Add(Chain(ClothChainKind.Cloak, "UpperChest", name, CloakBones, suffix));
                }
            }

            chains.Add(Chain(ClothChainKind.Hood, "Head", "Hood", HoodBones, string.Empty));
            return chains.ToArray();
        }

        private static ClothChain Chain(ClothChainKind kind, string parent, string name, int count, string suffix)
        {
            var bones = new string[count];
            for (var index = 0; index < count; index++)
            {
                bones[index] = name + (index + 1) + suffix;
            }

            return new ClothChain(kind, parent, bones);
        }
    }
}
