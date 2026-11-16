namespace Mmorpg.GameProtocol
{
    // Cross-field rules of the Combat family, its intent error codes, and the health rule of
    // its combatant state. The schema in Schema~ describes the messages, and the generator
    // writes their models and codecs.
    public static partial class RealtimeProtocol
    {
        // Each event kind carries only its own fields. A cancelled attack ended before its release
        // and names no tick.
        private static bool SatisfiesRules(RealtimeCombatEvent value)
        {
            switch (value.Kind)
            {
                case RealtimeCombatEventKind.AttackStarted:
                case RealtimeCombatEventKind.AttackReleased:
                case RealtimeCombatEventKind.AttackCancelled:
                    return value.SourceEntityId != 0
                        && value.SourceEntityId != value.TargetEntityId
                        && (value.Kind != RealtimeCombatEventKind.AttackCancelled || value.Tick == 0)
                        && value.Damage == 0
                        && value.DamageType == RealtimeCombatDamageType.Normal
                        && value.HealthAfter == 0;
                case RealtimeCombatEventKind.Hit:
                    return value.Tick == 0
                        && value.AttackStyle == RealtimeCombatAttackStyle.Melee;
                case RealtimeCombatEventKind.Died:
                    return value.Tick == 0
                        && value.Damage == 0
                        && value.DamageType == RealtimeCombatDamageType.Normal
                        && value.HealthAfter == 0
                        && value.AttackStyle == RealtimeCombatAttackStyle.Melee;
                default:
                    return false;
            }
        }

        // A dead combatant has no health, no target, and no regeneration, and nobody targets or
        // tags itself. Without a regeneration amount there is no regeneration tick.
        private static bool SatisfiesRules(RealtimeCombatantState value)
        {
            return value.Health <= value.MaximumHealth
                && (value.LifeState != RealtimeCombatLifeState.Dead
                    || (value.Health == 0
                        && value.TargetEntityId == 0
                        && value.HealthRegenerationAmount == 0))
                && value.TargetEntityId != value.EntityId
                && value.TaggedByEntityId != value.EntityId
                && (value.HealthRegenerationAmount != 0 || value.HealthRegenerationTick == 0);
        }

        // A character is never evading; while dead it waits for its respawn tick.
        private static bool SatisfiesRules(RealtimeOwnerCombatState value)
        {
            if (value.Health > value.MaximumHealth
                || value.Mana > value.MaximumMana
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

    // The health that a combatant state describes at a server tick. The worker sends a new
    // state only when something other than the regeneration changes, and every receiver
    // follows the regeneration between states with this one rule.
    public static class RealtimeCombatHealth
    {
        // The state's health plus its regeneration amount for every step whose tick the server
        // tick reached, one step every tickRateHz ticks from HealthRegenerationTick, at most the
        // maximum health. The state's own health when it regenerates nothing, the combatant is
        // not alive, or the tick rate is unknown. Ticks wrap, so a step tick up to half the tick
        // range behind the server tick counts as reached.
        public static int At(RealtimeCombatantState state, uint serverTick, int tickRateHz)
        {
            if (state == null)
            {
                throw new System.ArgumentNullException(nameof(state));
            }

            if (state.HealthRegenerationAmount <= 0
                || state.LifeState != RealtimeCombatLifeState.Alive
                || tickRateHz <= 0
                || state.Health >= state.MaximumHealth)
            {
                return state.Health;
            }

            var ticksSinceFirstStep = unchecked((int)(serverTick - state.HealthRegenerationTick));
            if (ticksSinceFirstStep < 0)
            {
                return state.Health;
            }

            var steps = ((long)ticksSinceFirstStep / tickRateHz) + 1;
            var health = state.Health + (steps * state.HealthRegenerationAmount);
            return health >= state.MaximumHealth ? state.MaximumHealth : (int)health;
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
