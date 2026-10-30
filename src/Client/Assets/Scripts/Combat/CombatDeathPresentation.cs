using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // Shows the life state of every player body in view from the combat state: the owner's
    // state for the local player and the combatant states for other players. A body falls
    // when its character dies in view, lies dead at once when it comes into view already
    // dead, and stands again when the character respawns. Each frame tells every body its
    // state, so a body that spawns, or whose state arrives later, catches up by itself. A
    // player without combat state, such as a development bot, never dies. A Mob's creature
    // with clips of its own, such as a wolf, falls when the Mob dies, until its corpse
    // replaces it.
    public sealed class CombatDeathPresentation : MonoBehaviour
    {
        private CombatClientController combatController;
        private CombatantViewLocator views;

        // Called by the composition root before the component is activated.
        public void Initialize(CombatClientController combat, CombatantViewLocator combatantViews)
        {
            combatController = combat;
            views = combatantViews;
        }

        private void Update()
        {
            if (combatController == null || views == null)
            {
                return;
            }

            var state = combatController.State;
            var player = views.LocalPlayer;
            var localBody = player != null ? player.Body : null;
            if (state.Owner != null && localBody != null && localBody.isActiveAndEnabled)
            {
                localBody.ShowLifeState(state.Owner.LifeState == RealtimeCombatLifeState.Dead);
            }

            foreach (var remote in views.RemotePlayers)
            {
                var body = remote != null ? remote.Body : null;
                if (body != null
                    && body.isActiveAndEnabled
                    && state.TryGetCombatant(remote.NetworkEntityId, out var combatant))
                {
                    body.ShowLifeState(combatant.LifeState == RealtimeCombatLifeState.Dead);
                }
            }

            foreach (var actor in views.WorldActors)
            {
                var creature = actor is MobView ? actor.GetComponent<CreatureAnimator>() : null;
                if (creature != null
                    && creature.isActiveAndEnabled
                    && state.TryGetCombatant(actor.TargetEntityId, out var mob))
                {
                    creature.ShowLifeState(mob.LifeState == RealtimeCombatLifeState.Dead);
                }
            }
        }
    }
}
