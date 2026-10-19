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

    public enum CombatClickAction
    {
        // The click goes on to world interaction or becomes a walk, and ends the attack.
        Pass,

        // The click ends here without any action, such as every click while dead.
        Ignore,

        // The click selects the target and attacks it.
        Attack
    }

    // Decides what a world click means for combat. The worker validates every attack again;
    // these rules only choose the intent and keep a dead character from acting.
    public static class CombatClickRules
    {
        public static bool IsDead(RealtimeOwnerCombatState owner)
        {
            return owner != null && owner.LifeState == RealtimeCombatLifeState.Dead;
        }

        // The nearest candidate decides, so a click never reaches past an NPC to a Mob
        // behind it. A Mob is only ever an attack target, never an interaction.
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

        // A dead character ignores every click. A living Mob is attacked, and so is a living
        // player when the Shard allows player versus player. A Mob that cannot be attacked
        // now, such as one walking home, takes the click without action; a player that
        // cannot be attacked lets it pass, so the click walks.
        public static CombatClickAction Classify(
            CombatClickTarget target,
            RealtimeOwnerCombatState owner,
            bool playerVersusPlayer,
            RealtimeCombatantState targetState)
        {
            if (IsDead(owner))
            {
                return CombatClickAction.Ignore;
            }

            var alive = targetState != null
                && targetState.EntityId == target.EntityId
                && targetState.LifeState == RealtimeCombatLifeState.Alive;
            switch (target.Kind)
            {
                case CombatClickTargetKind.Mob:
                    return alive ? CombatClickAction.Attack : CombatClickAction.Ignore;
                case CombatClickTargetKind.Player:
                    return alive && playerVersusPlayer
                        ? CombatClickAction.Attack
                        : CombatClickAction.Pass;
                default:
                    return CombatClickAction.Pass;
            }
        }
    }
}
