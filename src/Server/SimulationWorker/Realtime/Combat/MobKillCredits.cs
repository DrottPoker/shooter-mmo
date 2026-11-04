using Mmorpg.SimulationWorker.Entities;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Realtime;

// A player whom a dying Mob credits with its kill. The player owns the Mob's loot, and quests
// and experience count the kill for it.
internal readonly record struct MobKillCredit(Guid CharacterId, ulong EntityId);

// Who a Mob credits, by its kill credit content. The first player who damages a tagged Mob in a
// fight tags it, while a world boss with contributor credit remembers every player who damaged
// it. As the Mob dies it credits those players who are still in the world within the kill
// credit range of it. Loop-confined.
internal static class MobKillCredits
{
    public static void RecordDamage(Combatant mob, Combatant attacker)
    {
        if (mob.Kind != CombatantKind.Mob
            || mob.Actor is null
            || attacker.Kind != CombatantKind.Player
            || attacker.Player is null)
        {
            return;
        }

        var characterId = attacker.Player.Session.CharacterId;
        switch (mob.Actor.Definition.KillCredit)
        {
            case WorldActorKillCreditIds.Tag:
                mob.Tag(attacker.EntityId, characterId);
                break;
            case WorldActorKillCreditIds.Contributors:
                mob.Contributors.Add(characterId);
                break;
        }
    }

    // Credited players are ordered by character id, so loot rolls stay reproducible.
    public static IReadOnlyList<MobKillCredit> Resolve(
        Combatant mob,
        SimulationEntityRegistry players,
        float rangeMeters)
    {
        if (mob.Actor is null)
        {
            return [];
        }

        if (string.Equals(mob.Actor.Definition.KillCredit, WorldActorKillCreditIds.Contributors, StringComparison.Ordinal))
        {
            var credits = new List<MobKillCredit>(mob.Contributors.Count);
            foreach (var characterId in mob.Contributors.OrderBy(id => id))
            {
                if (TryCredit(mob, characterId, players, rangeMeters, out var credit))
                {
                    credits.Add(credit);
                }
            }

            return credits;
        }

        return mob.TagEntityId != 0 && TryCredit(mob, mob.TagCharacterId, players, rangeMeters, out var tagger)
            ? [tagger]
            : [];
    }

    // Dead or alive, a player counts while it is in the world within range of the Mob.
    private static bool TryCredit(
        Combatant mob,
        Guid characterId,
        SimulationEntityRegistry players,
        float rangeMeters,
        out MobKillCredit credit)
    {
        credit = default;
        if (!players.TryGetPlayerByCharacterId(characterId, out var player) || player is null)
        {
            return false;
        }

        var state = player.Movement.State;
        var deltaX = state.PositionX - mob.Actor!.PositionX;
        var deltaY = state.PositionY - mob.Actor.PositionY;
        var deltaZ = state.PositionZ - mob.Actor.PositionZ;
        if ((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) > rangeMeters * rangeMeters)
        {
            return false;
        }

        credit = new MobKillCredit(characterId, player.NetworkEntityId);
        return true;
    }
}
