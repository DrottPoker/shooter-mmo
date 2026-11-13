using Mmorpg.Server.Common.Items;
using Mmorpg.WorldData.Items;

namespace Mmorpg.Tools.StackStressGenerator;

// One bot's part in the playing workload: its role, home ground, kit, and the seed of its
// decisions. The same scenario, seed, and bot indices give every bot the same assignment.
public sealed record PlayingAssignment(
    int BotIndex,
    PlayingRole Role,
    PlayingGround? Home,
    string? Kit,
    int DecisionSeed);

public static class PlayingRoster
{
    // The kit names of tools/StackStressGenerator and of the StackStressFixtures:Kits settings.
    public const string SwordKit = "sword";
    public const string BowKit = "bow";

    public static IReadOnlyList<PlayingAssignment> Assign(
        PlayingScenario scenario,
        int botCount,
        int botStartIndex,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfLessThan(botCount, 1);
        var roles = RoleCounts(scenario, botCount)
            .SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value))
            .ToArray();
        Shuffle(roles, new Random(Mix(seed, 0x524f4c45)));

        var fighters = roles.Count(role => role == PlayingRole.Fighter);
        var bows = (int)Math.Round(fighters * scenario.FighterBowPercent / 100d, MidpointRounding.AwayFromZero);
        var fighterOrder = Enumerable.Range(0, fighters).ToArray();
        Shuffle(fighterOrder, new Random(Mix(seed, 0x424f5753)));
        var bowFighters = fighterOrder.Take(bows).ToHashSet();

        var ordinals = new Dictionary<PlayingRole, int>();
        var assignments = new PlayingAssignment[botCount];
        for (var offset = 0; offset < botCount; offset++)
        {
            var role = roles[offset];
            var ordinal = ordinals.GetValueOrDefault(role);
            ordinals[role] = ordinal + 1;
            var kind = PlayingScenario.GroundKindOf(role);
            var grounds = kind is null ? [] : scenario.GroundsOf(kind.Value);
            var home = grounds.Count == 0 ? null : grounds[ordinal % grounds.Count];
            var kit = role == PlayingRole.Fighter
                ? bowFighters.Contains(ordinal) ? BowKit : SwordKit
                : null;
            var botIndex = botStartIndex + offset;
            assignments[offset] = new PlayingAssignment(botIndex, role, home, kit, Mix(seed, botIndex));
        }

        return assignments;
    }

    // The bot count of every role by the largest remainder of its share, so the counts add up
    // to the bot count and follow the shares as closely as whole bots can.
    public static IReadOnlyDictionary<PlayingRole, int> RoleCounts(PlayingScenario scenario, int botCount)
    {
        var exact = Enum.GetValues<PlayingRole>()
            .Select(role => (Role: role, Value: botCount * scenario.RoleShares.GetValueOrDefault(role) / 100d))
            .ToArray();
        var counts = exact.ToDictionary(entry => entry.Role, entry => (int)Math.Floor(entry.Value));
        var remaining = botCount - counts.Values.Sum();
        foreach (var entry in exact
                     .Where(entry => scenario.RoleShares.GetValueOrDefault(entry.Role) > 0)
                     .OrderByDescending(entry => entry.Value - Math.Floor(entry.Value))
                     .ThenBy(entry => entry.Role)
                     .Take(remaining))
        {
            counts[entry.Role]++;
        }

        return counts;
    }

    // The kit items with the equipment slot a player equips each into and the weapon's range,
    // read from the item catalog as the Unity client reads it.
    public static PlayingKit ResolveKit(
        string? kitName,
        IEnumerable<string> kitDefinitionIds,
        ItemCatalogRuntimeDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (kitName is null)
        {
            return PlayingKit.None;
        }

        var items = new List<PlayingKitItem>();
        var range = 0f;
        foreach (var definitionId in kitDefinitionIds.Distinct(StringComparer.Ordinal))
        {
            var definition = catalog.Definitions.FirstOrDefault(candidate => candidate.Id == definitionId)
                ?? throw new InvalidOperationException(
                    $"The kit '{kitName}' holds '{definitionId}', which the item catalog does not define.");
            if (definition.EquipmentSlots is not { Length: > 0 } slots)
            {
                continue;
            }

            items.Add(new PlayingKitItem(definitionId, slots[0]));
            if (definition.Weapon is { } weapon)
            {
                range = Math.Max(range, weapon.Range);
            }
        }

        if (range <= 0f)
        {
            throw new InvalidOperationException($"The kit '{kitName}' holds no weapon.");
        }

        return new PlayingKit(kitName, items, range);
    }

    public static ItemCatalogRuntimeDocument LoadItemCatalog(string path)
    {
        return ItemCatalogFileLoader.Load(path);
    }

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    // A seed per purpose and bot that is the same in every process, unlike System.HashCode.
    public static int Mix(int seed, int value)
    {
        unchecked
        {
            var mixed = ((uint)seed * 0x9E3779B1u) ^ ((uint)value * 0x85EBCA6Bu);
            mixed ^= mixed >> 15;
            mixed *= 0xC2B2AE35u;
            mixed ^= mixed >> 13;
            return (int)mixed;
        }
    }
}
