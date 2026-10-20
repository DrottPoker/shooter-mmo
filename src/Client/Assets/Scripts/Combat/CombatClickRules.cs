using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Combat
{
    public enum CombatClickTargetKind
    {
        // Nothing under the pointer, such as the ground.
        None,
        Mob,
        Player,

        // A target of another feature, such as an NPC, a resource node, or a corpse.
        Other
    }

    // What a world click points at, as combat sees it.
    public readonly struct CombatClickTarget
    {
        public CombatClickTarget(CombatClickTargetKind kind, ulong entityId)
        {
            Kind = kind;
            EntityId = kind == CombatClickTargetKind.Mob || kind == CombatClickTargetKind.Player
                ? entityId
                : 0;
        }

        public CombatClickTargetKind Kind { get; }

        public ulong EntityId { get; }
    }

    // Decides what combat offers in the world menu. The worker validates every attack again;
    // these rules only choose the intent and keep a dead character from acting.
    public static class CombatClickRules
    {
        public static bool IsDead(RealtimeOwnerCombatState owner)
        {
            return owner != null && owner.LifeState == RealtimeCombatLifeState.Dead;
        }

        // What a pointer candidate is to combat. A Mob is only ever an attack target, never an
        // interaction.
        public static CombatClickTarget Describe(
            WorldPointerCandidate candidate,
            WorldActorClientState actors)
        {
            if (candidate.IsEmpty || !candidate.IsActive)
            {
                return new CombatClickTarget(CombatClickTargetKind.None, 0);
            }

            if (candidate.Player != null)
            {
                return candidate.Player.NetworkEntityId == 0
                    ? new CombatClickTarget(CombatClickTargetKind.Other, 0)
                    : new CombatClickTarget(
                        CombatClickTargetKind.Player,
                        candidate.Player.NetworkEntityId);
            }

            var target = candidate.Target;
            if (target.TargetKind == RealtimeWorldInteractionTargetKind.WorldActor
                && actors != null
                && actors.TryGet(target.TargetEntityId, out var actor)
                && actor.Kind == RealtimeWorldActorKind.Mob)
            {
                return new CombatClickTarget(CombatClickTargetKind.Mob, actor.EntityId);
            }

            return new CombatClickTarget(CombatClickTargetKind.Other, 0);
        }

        // The entity that Attack on this candidate targets, or 0 when the candidate offers no
        // Attack now. The menu asks when it is built and again when a stored Attack is chosen,
        // because the target may have died or left in between.
        public static ulong AttackableTarget(
            WorldPointerCandidate candidate,
            WorldActorClientState actors,
            CombatClientState combat,
            bool playerVersusPlayer)
        {
            if (combat == null)
            {
                return 0;
            }

            var target = Describe(candidate, actors);
            if (target.EntityId == 0)
            {
                return 0;
            }

            combat.TryGetCombatant(target.EntityId, out var targetState);
            return CanAttack(target, combat.Owner, playerVersusPlayer, targetState)
                ? target.EntityId
                : 0;
        }

        // Whether the menu offers Attack: on a living Mob, and on a living player when the
        // Shard allows player versus player, never while the character is dead. A Mob that
        // cannot be attacked now, such as one walking home, offers no Attack, so a left click
        // on it walks there instead.
        public static bool CanAttack(
            CombatClickTarget target,
            RealtimeOwnerCombatState owner,
            bool playerVersusPlayer,
            RealtimeCombatantState targetState)
        {
            if (IsDead(owner))
            {
                return false;
            }

            var alive = targetState != null
                && targetState.EntityId == target.EntityId
                && targetState.LifeState == RealtimeCombatLifeState.Alive;
            switch (target.Kind)
            {
                case CombatClickTargetKind.Mob:
                    return alive;
                case CombatClickTargetKind.Player:
                    return alive && playerVersusPlayer;
                default:
                    return false;
            }
        }
    }
}
