using Mmorpg.SimulationWorker.Entities;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

// A player whom a dying Mob credits with its kill. The player owns the Mob's loot, and quests
// and experience count the kill for it.
internal readonly record struct MobKillCredit(Guid CharacterId, ulong EntityId);

// Who a Mob credits, by its kill credit content. The first player who damages a Mob in a fight
// tags it, and the tagger is credited when it is still within the kill credit range of the Mob
// as the Mob dies. Loop-confined.
internal static class MobKillCredits
{
    public static void RecordDamage(Combatant mob, Combatant attacker)
    {
        if (mob.Kind != CombatantKind.Mob
            || mob.Actor is null
            || attacker.Kind != CombatantKind.Player
            || attacker.Player is null
            || !string.Equals(mob.Actor.Definition.KillCredit, WorldActorKillCreditIds.Tag, StringComparison.Ordinal))
        {
            return;
        }

        mob.Tag(attacker.EntityId, attacker.Player.Session.CharacterId);
    }

    public static IReadOnlyList<MobKillCredit> Resolve(
        Combatant mob,
        SimulationEntityRegistry players,
        float rangeMeters)
    {
        if (mob.TagEntityId == 0
            || mob.Actor is null
            || !players.TryGetPlayerByCharacterId(mob.TagCharacterId, out var tagger)
            || tagger is null)
        {
            return [];
        }

        var state = tagger.Movement.State;
        var deltaX = state.PositionX - mob.Actor.PositionX;
        var deltaY = state.PositionY - mob.Actor.PositionY;
        var deltaZ = state.PositionZ - mob.Actor.PositionZ;
        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <= rangeMeters * rangeMeters
            ? [new MobKillCredit(mob.TagCharacterId, tagger.NetworkEntityId)]
            : [];
    }
}
