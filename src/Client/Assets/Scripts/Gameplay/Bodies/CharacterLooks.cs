using System.Collections.Generic;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Characters;

namespace Mmorpg.Client.Gameplay
{
    // A character's look as the server stores and sends it, choices of the kinds of
    // content/characters/appearance.json, and as the BodyLook a body shows: the face and the eyes
    // are options of the Face and Eye slots, underwear, scars, and tattoos are paint layers, and
    // hair is a garment of the hair slot. Options are named as the bodies' appearance catalogs name
    // them. See docs/EQUIPMENT_VISUALS_DESIGN.md, Items.
    public static class CharacterLooks
    {
        public const string FaceSlot = "Face";
        public const string EyeSlot = "Eye";

        // The look of the choices, without garments other than the hair.
        public static BodyLook LookOf(IReadOnlyList<RealtimeAppearanceChoice> choices)
        {
            var look = BodyLook.Default;
            if (choices == null)
            {
                return look;
            }

            foreach (var choice in choices)
            {
                if (choice == null || string.IsNullOrEmpty(choice.Option))
                {
                    continue;
                }

                switch (choice.Kind)
                {
                    case CharacterAppearanceFormat.Face:
                        look = look.With(FaceSlot, choice.Option);
                        break;
                    case CharacterAppearanceFormat.Eyes:
                        look = look.With(EyeSlot, choice.Option);
                        break;
                    case CharacterAppearanceFormat.Scar:
                    case CharacterAppearanceFormat.Tattoo:
                    case CharacterAppearanceFormat.Underwear:
                        look = look.WithPaint(choice.Option, true);
                        break;
                    case CharacterAppearanceFormat.Hair:
                        look = look.WithGarment(choice.Option, true);
                        break;
                }
            }

            return look;
        }

        // The choices of a look as the body's catalog knows them: what the catalog does not offer,
        // such as a garment that is not hair, is left out.
        public static RealtimeAppearanceChoice[] ChoicesOf(BodyLook look, BodyAppearanceCatalog catalog)
        {
            var choices = new List<RealtimeAppearanceChoice>();
            if (look == null || catalog == null)
            {
                return choices.ToArray();
            }

            AddSlot(choices, look, CharacterAppearanceFormat.Face, FaceSlot);
            AddSlot(choices, look, CharacterAppearanceFormat.Eyes, EyeSlot);
            foreach (var paint in catalog.Paints)
            {
                if (paint == null || !look.HasPaint(paint.Name))
                {
                    continue;
                }

                switch (paint.Category)
                {
                    case AppearanceCategory.Underwear:
                        choices.Add(new RealtimeAppearanceChoice(CharacterAppearanceFormat.Underwear, paint.Slot, paint.Name));
                        break;
                    case AppearanceCategory.Scar:
                        choices.Add(new RealtimeAppearanceChoice(CharacterAppearanceFormat.Scar, string.Empty, paint.Name));
                        break;
                    case AppearanceCategory.Tattoo:
                        choices.Add(new RealtimeAppearanceChoice(CharacterAppearanceFormat.Tattoo, string.Empty, paint.Name));
                        break;
                }
            }

            foreach (var name in look.Garments)
            {
                var garment = catalog.GarmentNamed(name);
                if (garment != null && garment.Slot == BodyAppearanceCatalog.Garment.HairSlot)
                {
                    choices.Add(new RealtimeAppearanceChoice(CharacterAppearanceFormat.Hair, string.Empty, name));
                }
            }

            return choices.ToArray();
        }

        // The items others see on the character, in the order the server sends them.
        public static EquippedItem[] EquipmentOf(RealtimeCharacterAppearance appearance)
        {
            if (appearance == null || appearance.Equipment == null)
            {
                return new EquippedItem[0];
            }

            var items = new EquippedItem[appearance.Equipment.Length];
            for (var index = 0; index < items.Length; index++)
            {
                var item = appearance.Equipment[index];
                items[index] = new EquippedItem(item.EquipmentSlotId, item.DefinitionId);
            }

            return items;
        }

        private static void AddSlot(List<RealtimeAppearanceChoice> choices, BodyLook look, string kind, string slot)
        {
            var option = look.OptionFor(slot);
            if (!string.IsNullOrEmpty(option))
            {
                choices.Add(new RealtimeAppearanceChoice(kind, string.Empty, option));
            }
        }
    }
}
