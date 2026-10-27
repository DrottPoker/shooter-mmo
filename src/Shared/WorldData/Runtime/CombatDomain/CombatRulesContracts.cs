using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Combat
{
    public static class CombatRulesFormat
    {
        public const int Version = 2;
        public const string FileName = "combat-rules.json";
        public const string DirectoryName = "combat";

        public const int MaximumPlayerHealth = CombatContentRules.MaximumHealth;
        public const int MaximumPlayerMana = CombatContentRules.MaximumMana;
        public const float MaximumRespawnDelaySeconds = 300f;
        public const float MaximumCombatTimeoutSeconds = 600f;
        public const float MaximumEquipmentLockoutSeconds = 30f;
        public const float MinimumAmmunitionBatchIntervalSeconds = 0.5f;
        public const float MaximumAmmunitionBatchIntervalSeconds = 60f;
        public const int MaximumRuleSets = 64;
        public const int MaximumRuleSetIdLength = 128;
    }

    // The worker-side combat rules that are not item or actor data: the character's health,
    // mana, and death timing, regeneration, the equipment lockout, ammunition batching, and what
    // each Shard rule set allows. The worker reads this file next to world.json.
    [Serializable]
    public sealed class CombatRulesDocument
    {
        public int formatVersion;
        public PlayerCombatRulesDocument player = new PlayerCombatRulesDocument();
        public AmmunitionRulesDocument ammunition = new AmmunitionRulesDocument();
        public List<ShardRuleSetDocument> ruleSets = new List<ShardRuleSetDocument>();
    }

    [Serializable]
    public sealed class PlayerCombatRulesDocument
    {
        public int maximumHealth;

        // The mana a character casts magic with; 0 leaves it without magic.
        public int maximumMana;

        // A dead character respawns at the World spawn this long after it died.
        public float respawnDelaySeconds;

        // A combatant stays in combat this long after its last attack or hit.
        public float combatTimeoutSeconds;

        // Out of combat a character regains this much health and mana every second.
        public int healthRegenerationPerSecond;
        public int manaRegenerationPerSecond;

        // Attacks wait this long after the item in an equipment slot changed.
        public float equipmentLockoutSeconds;
    }

    [Serializable]
    public sealed class AmmunitionRulesDocument
    {
        // Spent arrows commit at most this long after the first uncommitted shot.
        public float batchIntervalSeconds;
    }

    [Serializable]
    public sealed class ShardRuleSetDocument
    {
        public string id = string.Empty;
        public bool playerVersusPlayer;
    }

    public sealed class CombatRulesValidationException : Exception
    {
        public CombatRulesValidationException(string message)
            : base(message)
        {
        }
    }

    public static class CombatRulesValidator
    {
        public static void Validate(CombatRulesDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.formatVersion != CombatRulesFormat.Version)
            {
                throw new CombatRulesValidationException("Combat rules format version is unsupported.");
            }

            var player = document.player
                ?? throw new CombatRulesValidationException("Player combat rules are required.");
            if (player.maximumHealth < 1 || player.maximumHealth > CombatRulesFormat.MaximumPlayerHealth)
            {
                throw new CombatRulesValidationException(
                    "Player.MaximumHealth must be between 1 and " + CombatRulesFormat.MaximumPlayerHealth + ".");
            }

            ValidateAmount(player.maximumMana, CombatRulesFormat.MaximumPlayerMana, "Player.MaximumMana");
            ValidateSeconds(player.respawnDelaySeconds, 0f, CombatRulesFormat.MaximumRespawnDelaySeconds, "Player.RespawnDelaySeconds");
            ValidateSeconds(player.combatTimeoutSeconds, 0f, CombatRulesFormat.MaximumCombatTimeoutSeconds, "Player.CombatTimeoutSeconds");
            ValidateAmount(player.healthRegenerationPerSecond, player.maximumHealth, "Player.HealthRegenerationPerSecond");
            ValidateAmount(player.manaRegenerationPerSecond, player.maximumMana, "Player.ManaRegenerationPerSecond");

            ValidateSeconds(
                player.equipmentLockoutSeconds,
                0f,
                CombatRulesFormat.MaximumEquipmentLockoutSeconds,
                "Player.EquipmentLockoutSeconds");
            var ammunition = document.ammunition
                ?? throw new CombatRulesValidationException("Ammunition rules are required.");
            ValidateSeconds(
                ammunition.batchIntervalSeconds,
                CombatRulesFormat.MinimumAmmunitionBatchIntervalSeconds,
                CombatRulesFormat.MaximumAmmunitionBatchIntervalSeconds,
                "Ammunition.BatchIntervalSeconds");
            ValidateRuleSets(document.ruleSets);
        }

        private static void ValidateRuleSets(List<ShardRuleSetDocument> ruleSets)
        {
            if (ruleSets == null || ruleSets.Count == 0 || ruleSets.Count > CombatRulesFormat.MaximumRuleSets)
            {
                throw new CombatRulesValidationException(
                    "RuleSets must hold between 1 and " + CombatRulesFormat.MaximumRuleSets + " rule sets.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            string previous = null;
            foreach (var ruleSet in ruleSets)
            {
                if (ruleSet == null || !IsRuleSetId(ruleSet.id))
                {
                    throw new CombatRulesValidationException(
                        "Every rule set needs a lowercase id of letters, digits, dots, hyphens, and underscores.");
                }

                if (!ids.Add(ruleSet.id))
                {
                    throw new CombatRulesValidationException("Rule set " + ruleSet.id + " is defined more than once.");
                }

                if (previous != null && string.CompareOrdinal(previous, ruleSet.id) > 0)
                {
                    throw new CombatRulesValidationException("Rule sets must be sorted by id.");
                }

                previous = ruleSet.id;
            }
        }

        private static void ValidateAmount(int value, int maximum, string name)
        {
            if (value < 0 || value > maximum)
            {
                throw new CombatRulesValidationException(name + " must be between 0 and " + maximum + ".");
            }
        }

        private static void ValidateSeconds(float value, float minimum, float maximum, string name)
        {
            if (!CombatContentRules.IsFinite(value) || value < minimum || value > maximum)
            {
                throw new CombatRulesValidationException(
                    name + " must be between " + minimum + " and " + maximum + " seconds.");
            }
        }

        private static bool IsRuleSetId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > CombatRulesFormat.MaximumRuleSetIdLength)
            {
                return false;
            }

            foreach (var character in value)
            {
                var valid = (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '-'
                    || character == '_';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
