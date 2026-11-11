using System.Globalization;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // Labels and values for the temporary combat HUD. Everything here reads authoritative
    // combat state only.
    public static class CombatHudPresentation
    {
        public const string UnknownStatusKey = "ui/combat/unknown-status";
        public static readonly Color NormalDamageColor = Color.white;
        public static readonly Color ManaColor = new Color(0.27f, 0.55f, 1f, 1f);

        // A short label for every attack status the worker reports.
        public static string AttackStatusLabel(RealtimeCombatAttackStatus status, TextCatalog texts)
        {
            return texts.Get(AttackStatusKey(status));
        }

        private static string AttackStatusKey(RealtimeCombatAttackStatus status)
        {
            switch (status)
            {
                case RealtimeCombatAttackStatus.Idle:
                    return "ui/combat/status-idle";
                case RealtimeCombatAttackStatus.Attacking:
                    return "ui/combat/status-attacking";
                case RealtimeCombatAttackStatus.Moving:
                    return "ui/combat/status-moving";
                case RealtimeCombatAttackStatus.OutOfRange:
                    return "ui/combat/status-out-of-range";
                case RealtimeCombatAttackStatus.LineOfSightBlocked:
                    return "ui/combat/status-no-line-of-sight";
                case RealtimeCombatAttackStatus.EquipmentLockout:
                    return "ui/combat/status-equipment-changed";
                case RealtimeCombatAttackStatus.WeaponMissing:
                    return "ui/combat/status-no-weapon";
                case RealtimeCombatAttackStatus.AmmunitionMissing:
                    return "ui/combat/status-no-ammunition";
                case RealtimeCombatAttackStatus.AmmunitionIncompatible:
                    return "ui/combat/status-wrong-ammunition";
                case RealtimeCombatAttackStatus.NotEnoughMana:
                    return "ui/combat/status-not-enough-mana";
                case RealtimeCombatAttackStatus.Dead:
                    return "ui/combat/status-dead";
                default:
                    return UnknownStatusKey;
            }
        }

        public static string HealthText(int health, int maximumHealth)
        {
            return health.ToString(CultureInfo.InvariantCulture)
                + " / " + maximumHealth.ToString(CultureInfo.InvariantCulture);
        }

        public static float HealthFraction(int health, int maximumHealth)
        {
            return maximumHealth <= 0 ? 0f : Mathf.Clamp01(health / (float)maximumHealth);
        }

        // Mana uses the health format; a character without mana shows none.
        public static string ManaText(int mana, int maximumMana, TextCatalog texts)
        {
            return maximumMana > 0
                ? texts.Format(
                    "ui/combat/mana",
                    MessageArgument.Text("mana", mana.ToString(CultureInfo.InvariantCulture)),
                    MessageArgument.Text("maximum", maximumMana.ToString(CultureInfo.InvariantCulture)))
                : texts.Get("ui/combat/no-mana");
        }

        // Green while healthy, amber below half, red below a quarter.
        public static Color HealthColor(float fraction)
        {
            if (fraction <= 0.25f)
            {
                return TemporaryUiKit.DangerColor;
            }

            return fraction <= 0.5f ? TemporaryUiKit.WarningColor : TemporaryUiKit.SuccessColor;
        }

        // A health bar shows over a living combatant that has lost health.
        public static bool ShowsHealthBar(int health, int maximumHealth, RealtimeCombatLifeState lifeState)
        {
            return lifeState != RealtimeCombatLifeState.Dead
                && maximumHealth > 0
                && health < maximumHealth;
        }

        public static string ArrowCountText(bool hasAmmunition, int quantity, TextCatalog texts)
        {
            return hasAmmunition
                ? texts.Format("ui/combat/arrows", MessageArgument.Text("count", quantity.ToString(CultureInfo.InvariantCulture)))
                : texts.Get("ui/combat/no-arrows");
        }

        // The whole seconds left, rounded up, so the countdown reaches 0 when the respawn is due.
        public static string RespawnCountdownText(bool clockKnown, float secondsRemaining, TextCatalog texts)
        {
            if (!clockKnown)
            {
                return texts.Get("ui/combat/respawning-soon");
            }

            var seconds = Mathf.CeilToInt(Mathf.Max(0f, secondsRemaining));
            return seconds > 0
                ? texts.Format("ui/combat/respawning-in", MessageArgument.Text("seconds", seconds.ToString(CultureInfo.InvariantCulture)))
                : texts.Get("ui/combat/respawning");
        }

        // Normal damage is white; the elements have their own tint until final art.
        public static Color DamageColor(RealtimeCombatDamageType damageType)
        {
            switch (damageType)
            {
                case RealtimeCombatDamageType.Fire:
                    return new Color(1f, 0.55f, 0.25f, 1f);
                case RealtimeCombatDamageType.Cold:
                    return new Color(0.55f, 0.85f, 1f, 1f);
                case RealtimeCombatDamageType.Nature:
                    return new Color(0.55f, 0.95f, 0.45f, 1f);
                case RealtimeCombatDamageType.Shadow:
                    return new Color(0.75f, 0.55f, 1f, 1f);
                default:
                    return NormalDamageColor;
            }
        }
    }
}
