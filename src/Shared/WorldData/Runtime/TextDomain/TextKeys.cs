using System;

namespace Mmorpg.WorldData.Text
{
    // The key of every content text is derived from the id of what it describes, so content
    // never spells a key out. A key is a path of segments separated by '/', and a segment is
    // a content id, which may itself contain dots: "item/weapon.iron_sword/name".
    public static class TextKeys
    {
        public const char Separator = '/';

        public static string ItemName(string definitionId)
        {
            return Join("item", definitionId, "name");
        }

        public static string ItemCategoryName(string categoryId)
        {
            return Join("item-category", categoryId, "name");
        }

        public static string ItemTagName(string tagId)
        {
            return Join("item-tag", tagId, "name");
        }

        public static string ToolKindName(string toolKindId)
        {
            return Join("tool-kind", toolKindId, "name");
        }

        public static string EquipmentSlotName(string equipmentSlotId)
        {
            return Join("equipment-slot", equipmentSlotId, "name");
        }

        public static string WeaponFamilyName(string weaponFamilyId)
        {
            return Join("weapon-family", weaponFamilyId, "name");
        }

        public static string AmmunitionFamilyName(string ammunitionFamilyId)
        {
            return Join("ammunition-family", ammunitionFamilyId, "name");
        }

        public static string VendorName(string vendorId)
        {
            return Join("vendor", vendorId, "name");
        }

        public static string CurrencyName()
        {
            return Join("currency", "name");
        }

        public static string ActorName(string actorDefinitionId)
        {
            return Join("actor", actorDefinitionId, "name");
        }

        public static string ActorExamine(string actorDefinitionId)
        {
            return Join("actor", actorDefinitionId, "examine");
        }

        public static string ActorCapabilityName(string actorDefinitionId, string capabilityId)
        {
            return Join("actor", actorDefinitionId, "capability", capabilityId, "name");
        }

        // The verb that the world menu shows before the actor's name, such as "Trade".
        public static string ActorCapabilityMenuOption(string actorDefinitionId, string capabilityId)
        {
            return Join("actor", actorDefinitionId, "capability", capabilityId, "menu");
        }

        public static string FactionName(string factionId)
        {
            return Join("faction", factionId, "name");
        }

        public static string QuestTitle(string questId)
        {
            return Join("quest", questId, "title");
        }

        public static string QuestOffer(string questId)
        {
            return Join("quest", questId, "offer");
        }

        public static string QuestProgress(string questId)
        {
            return Join("quest", questId, "progress");
        }

        public static string QuestCompletion(string questId)
        {
            return Join("quest", questId, "completion");
        }

        public static string QuestObjectiveText(string questId, string stepId, string objectiveId)
        {
            return Join("quest", questId, "step", stepId, "objective", objectiveId, "text");
        }

        // What the NPC of a talk-to objective says.
        public static string QuestObjectiveLine(string questId, string stepId, string objectiveId)
        {
            return Join("quest", questId, "step", stepId, "objective", objectiveId, "line");
        }

        // A key has at least two segments, and every segment is made of lowercase letters,
        // digits, '.', '_', and '-', starting and ending with a letter or digit.
        public static bool IsValid(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > TextTableFormat.MaximumKeyLength)
            {
                return false;
            }

            var segments = key.Split(Separator);
            if (segments.Length < 2)
            {
                return false;
            }

            foreach (var segment in segments)
            {
                if (!IsValidSegment(segment))
                {
                    return false;
                }
            }

            return true;
        }

        private static string Join(params string[] segments)
        {
            foreach (var segment in segments)
            {
                if (!IsValidSegment(segment))
                {
                    throw new ArgumentException(
                        "'" + segment + "' cannot be part of a text key. Use lowercase letters, "
                        + "digits, '.', '_', or '-', starting and ending with a letter or digit.");
                }
            }

            return string.Join(Separator.ToString(), segments);
        }

        private static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)
                || !IsLowerAsciiLetterOrDigit(segment[0])
                || !IsLowerAsciiLetterOrDigit(segment[segment.Length - 1]))
            {
                return false;
            }

            foreach (var character in segment)
            {
                if (!IsLowerAsciiLetterOrDigit(character)
                    && character != '.'
                    && character != '_'
                    && character != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLowerAsciiLetterOrDigit(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');
        }
    }
}
