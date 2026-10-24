using System;

namespace Mmorpg.Client.Gameplay
{
    // The kinds of a character's look painted on its body, as Victor decided on 2026-10-24: the
    // face and the eyes are images of their materials, the others paint layers over the skin.
    public enum AppearanceCategory
    {
        Face,
        Eye,
        // Always worn, one per visual slot it has, such as a bra on the chest and briefs on the legs.
        Underwear,
        Scar,
        Tattoo,
    }

    // When a player chooses each kind of look and how many a body shows. Character creation, the
    // look a server stores, and the NPC who changes a tattoo come later and follow these rules;
    // until then F2 and the Wardrobe show them.
    public static class AppearanceRules
    {
        public readonly struct Rule
        {
            public Rule(bool required, bool changesInGame)
            {
                Required = required;
                ChangesInGame = changesInGame;
            }

            // Exactly one is shown when true, at most one otherwise.
            public bool Required { get; }

            // Chosen in character creation, and changed in the game later when true.
            public bool ChangesInGame { get; }
        }

        // The body model, such as Human_Female: exactly one, chosen in character creation only.
        public static readonly Rule BodyModel = new Rule(true, false);

        public static Rule For(AppearanceCategory category)
        {
            switch (category)
            {
                case AppearanceCategory.Face:
                case AppearanceCategory.Eye:
                    return new Rule(true, false);
                case AppearanceCategory.Underwear:
                    return new Rule(true, true);
                case AppearanceCategory.Scar:
                    return new Rule(false, false);
                case AppearanceCategory.Tattoo:
                    return new Rule(false, true);
                default:
                    throw new ArgumentOutOfRangeException(nameof(category), category, null);
            }
        }

        // The category of an appearance slot of a catalog, named after its material, such as Face.
        public static AppearanceCategory? OfSlot(string slot)
        {
            return Enum.TryParse(slot, false, out AppearanceCategory category)
                && (category == AppearanceCategory.Face || category == AppearanceCategory.Eye)
                ? category
                : (AppearanceCategory?)null;
        }
    }
}
