using System;
using System.Collections.Generic;

namespace Mmorpg.WorldData.Combat
{
    // The combat triangle of the combat design. Code gives each style its meaning, so the
    // set is closed: Ranged attacks consume ammunition.
    public static class CombatStyleIds
    {
        public const string Melee = "melee";
        public const string Ranged = "ranged";
        public const string Magic = "magic";

        private static readonly string[] Values = { Melee, Ranged, Magic };

        public static IReadOnlyList<string> All => Values;

        public static bool IsKnown(string value)
        {
            return value != null && Array.IndexOf(Values, value) >= 0;
        }
    }

    // The damage types of the combat design. Normal is the default; each element has a
    // fixed presentation color, so the set is closed.
    public static class DamageTypeIds
    {
        public const string Normal = "normal";
        public const string Fire = "fire";
        public const string Cold = "cold";
        public const string Nature = "nature";
        public const string Shadow = "shadow";

        private static readonly string[] Values = { Normal, Fire, Cold, Nature, Shadow };

        public static IReadOnlyList<string> All => Values;

        public static bool IsKnown(string value)
        {
            return value != null && Array.IndexOf(Values, value) >= 0;
        }
    }

    // Bounds shared by the item and actor compilers for every attack in content.
    public static class CombatContentRules
    {
        public const int MaximumDamage = 100_000;
        public const int MaximumHealth = 1_000_000;
        public const float MinimumAttackIntervalSeconds = 0.5f;
        public const float MaximumAttackIntervalSeconds = 60f;
        public const float MaximumAttackRangeMetres = 64f;
        public const float MaximumProjectileSpeedMetresPerSecond = 200f;
        public const int MaximumAmmunitionDamageBonusPercent = 1000;
        public const int MaximumCharacterLevel = 10_000;

        public static bool IsValidAttackInterval(float seconds)
        {
            return IsFinite(seconds)
                && seconds >= MinimumAttackIntervalSeconds
                && seconds <= MaximumAttackIntervalSeconds;
        }

        public static bool IsValidAttackRange(float metres)
        {
            return IsFinite(metres) && metres > 0f && metres <= MaximumAttackRangeMetres;
        }

        public static bool IsValidDamageRange(int minimum, int maximum)
        {
            return minimum >= 0 && maximum >= minimum && maximum <= MaximumDamage;
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
