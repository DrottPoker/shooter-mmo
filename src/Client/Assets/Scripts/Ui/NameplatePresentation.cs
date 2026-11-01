using System.Globalization;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The rules of the nameplates over Mobs: which Mobs show one, the color of the health bar,
    // and its texts. Everything here reads authoritative state only.
    public static class NameplatePresentation
    {
        // Every living Mob this near the local character shows its nameplate.
        public const float MobRangeMetres = 40f;

        // How long the bar and its numbers take to reach a new health value.
        public const float HealthSlideSeconds = 0.3f;

        // Red for a Mob that attacks the players it detects, yellow for one that fights only once
        // it is damaged.
        public static readonly Color AggressiveColor = new Color(0.86f, 0.16f, 0.12f, 1f);
        public static readonly Color DefensiveColor = new Color(0.95f, 0.78f, 0.12f, 1f);

        // A Mob without a known aggression shows as aggressive, the safe assumption.
        public static Color MobHealthColor(RealtimeWorldActorAggression aggression)
        {
            return aggression == RealtimeWorldActorAggression.Defensive
                ? DefensiveColor
                : AggressiveColor;
        }

        public static string HealthText(int health, int maximumHealth)
        {
            return health.ToString(CultureInfo.InvariantCulture)
                + "/" + maximumHealth.ToString(CultureInfo.InvariantCulture);
        }

        public static string LevelText(int level)
        {
            return level > 0 ? level.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        // A living Mob within range of the local character shows its nameplate, whatever its health.
        public static bool ShowsMobNameplate(float sqrDistance, RealtimeCombatLifeState lifeState)
        {
            return lifeState != RealtimeCombatLifeState.Dead
                && sqrDistance <= MobRangeMetres * MobRangeMetres;
        }
    }
}
