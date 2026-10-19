using System.Globalization;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // Labels and values for the temporary combat HUD. Everything here reads authoritative
    // combat state only.
    public static class CombatHudPresentation
    {
        public const string UnknownStatusLabel = "Unknown";
        public static readonly Color NormalDamageColor = Color.white;

        // A short label for every attack status the worker reports.
        public static string AttackStatusLabel(RealtimeCombatAttackStatus status)
        {
            switch (status)
            {
                case RealtimeCombatAttackStatus.Idle:
                    return "No target";
                case RealtimeCombatAttackStatus.Attacking:
                    return "Attacking";
                case RealtimeCombatAttackStatus.Moving:
                    return "Stop moving to attack";
                case RealtimeCombatAttackStatus.OutOfRange:
                    return "Out of range";
                case RealtimeCombatAttackStatus.LineOfSightBlocked:
                    return "No line of sight";
                case RealtimeCombatAttackStatus.EquipmentLockout:
                    return "Equipment changed";
                case RealtimeCombatAttackStatus.WeaponMissing:
                    return "No weapon equipped";
                case RealtimeCombatAttackStatus.AmmunitionMissing:
                    return "No ammunition";
                case RealtimeCombatAttackStatus.AmmunitionIncompatible:
                    return "Wrong ammunition";
                case RealtimeCombatAttackStatus.Dead:
                    return "Dead";
                default:
                    return UnknownStatusLabel;
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

        public static string ArrowCountText(bool hasAmmunition, int quantity)
        {
            return hasAmmunition
                ? "Arrows " + quantity.ToString(CultureInfo.InvariantCulture)
                : "No arrows equipped";
        }

        // The whole seconds left, rounded up, so the countdown reaches 0 when the respawn is due.
        public static string RespawnCountdownText(bool clockKnown, float secondsRemaining)
        {
            if (!clockKnown)
            {
                return "Respawning soon";
            }

            var seconds = Mathf.CeilToInt(Mathf.Max(0f, secondsRemaining));
            return seconds > 0
                ? "Respawning in " + seconds.ToString(CultureInfo.InvariantCulture) + " s"
                : "Respawning...";
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
