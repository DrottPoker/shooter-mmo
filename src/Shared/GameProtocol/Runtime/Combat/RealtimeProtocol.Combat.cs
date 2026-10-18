namespace Mmorpg.GameProtocol
{
    // Cross-field rules of the Combat family. The schema in Schema~ describes the
    // messages, and the generator writes their models and codecs.
    public static partial class RealtimeProtocol
    {
        // Each event kind carries only its own fields.
        private static bool SatisfiesRules(RealtimeCombatEvent value)
        {
            switch (value.Kind)
            {
                case RealtimeCombatEventKind.AttackReleased:
                    return value.SourceEntityId != 0
                        && value.SourceEntityId != value.TargetEntityId
                        && value.Damage == 0
                        && value.DamageType == RealtimeCombatDamageType.Normal
                        && value.HealthAfter == 0;
                case RealtimeCombatEventKind.Hit:
                    return value.ImpactTick == 0;
                case RealtimeCombatEventKind.Died:
                    return value.ImpactTick == 0
                        && value.Damage == 0
                        && value.DamageType == RealtimeCombatDamageType.Normal
                        && value.HealthAfter == 0;
                default:
                    return false;
            }
        }

        // A dead combatant has no health and no target.
        private static bool SatisfiesRules(RealtimeCombatantState value)
        {
            return value.Health <= value.MaximumHealth
                && (value.LifeState != RealtimeCombatLifeState.Dead
                    || (value.Health == 0 && value.TargetEntityId == 0))
                && value.TargetEntityId != value.EntityId;
        }

        // A character is never evading; while dead it waits for its respawn tick.
        private static bool SatisfiesRules(RealtimeOwnerCombatState value)
        {
            if (value.Health > value.MaximumHealth
                || value.LifeState == RealtimeCombatLifeState.Evading)
            {
                return false;
            }

            if (value.LifeState == RealtimeCombatLifeState.Dead)
            {
                return value.Health == 0
                    && value.TargetEntityId == 0
                    && value.AttackStatus == RealtimeCombatAttackStatus.Dead;
            }

            return value.RespawnTick == 0
                && value.AttackStatus != RealtimeCombatAttackStatus.Dead
                && (value.TargetEntityId != 0
                    || value.AttackStatus == RealtimeCombatAttackStatus.Idle)
                && (value.AmmunitionItemInstanceId != System.Guid.Empty
                    || value.AmmunitionQuantity == 0);
        }
    }

    // The stable error codes of combat intents. Conditions that change every tick are
    // attack statuses, not errors.
    public static class RealtimeCombatErrorCodes
    {
        public const string TargetNotFound = "combat_target_not_found";
        public const string TargetNotAttackable = "combat_target_not_attackable";
        public const string TargetDead = "combat_target_dead";
        public const string TargetSelf = "combat_target_self";
        public const string PlayerVersusPlayerDisabled = "combat_player_versus_player_disabled";
        public const string CharacterDead = "combat_character_dead";
        public const string IntentInvalid = "combat_intent_invalid";
    }
}
