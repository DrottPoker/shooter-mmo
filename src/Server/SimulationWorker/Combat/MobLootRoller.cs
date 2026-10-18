using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.WorldData.Actors;

namespace Mmorpg.SimulationWorker.Combat;

// Rolls a Mob's loot profile in entry order: each entry drops with its chance in basis
// points and a quantity between its bounds. An empty roll leaves no corpse.
public static class MobLootRoller
{
    public const int BasisPointsPerWhole = 10_000;

    public static IReadOnlyList<MobCorpseLootSeed> Roll(
        WorldActorLootProfileDefinition? profile,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (profile?.Entries is null || profile.Entries.Length == 0)
        {
            return Array.Empty<MobCorpseLootSeed>();
        }

        var loot = new List<MobCorpseLootSeed>(profile.Entries.Length);
        foreach (var entry in profile.Entries)
        {
            if (entry.ChanceBasisPoints < BasisPointsPerWhole
                && random.NextInclusive(0, BasisPointsPerWhole - 1) >= entry.ChanceBasisPoints)
            {
                continue;
            }

            var quantity = random.NextInclusive(entry.MinimumQuantity, entry.MaximumQuantity);
            if (quantity > 0)
            {
                loot.Add(new MobCorpseLootSeed(entry.DefinitionId, quantity));
            }
        }

        return loot;
    }
}
