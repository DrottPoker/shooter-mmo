namespace Mmorpg.Tools.StackStressGenerator;

// The operations of the playing workload, as the report names them.
public static class PlayingOperations
{
    public const string Equip = "equip";
    public const string Teleport = "teleport";
    public const string Attack = "attack";
    public const string ClearTarget = "attack_clear";
    public const string LootOpen = "loot_open";
    public const string LootAll = "loot_all";
    public const string LootClose = "loot_close";

    public static readonly IReadOnlyList<string> All =
    [
        Equip,
        Teleport,
        Attack,
        ClearTarget,
        LootOpen,
        LootAll,
        LootClose
    ];
}

// Every rejection a playing bot may legitimately meet, per operation, with the reason it
// happens to a correct client. Everything else is unexpected and fails the run. A rejection
// nobody can explain is a finding, so this table only grows with a reason.
public static class PlayingExpectedConflicts
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Table =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [PlayingOperations.Equip] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_state_conflict"] =
                    "The character's item state changed between the intent and its handling: spent arrows commit in batches without an intent, and a death or a loot moves items too."
            },
            [PlayingOperations.Teleport] = new Dictionary<string, string>(StringComparer.Ordinal),
            [PlayingOperations.Attack] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["combat_target_dead"] =
                    "The Mob died, to this bot or another player, after the bot chose it and before the intent arrived.",
                ["combat_target_not_found"] =
                    "The Mob left the bot's view, by despawning after its death or by walking out of interest, before the intent arrived.",
                ["combat_target_not_attackable"] =
                    "The Mob began to evade, walking home after it lost its target, before the intent arrived.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it decided to attack and before the intent arrived."
            },
            [PlayingOperations.ClearTarget] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["combat_character_dead"] =
                    "A Mob killed the bot after it decided to stop attacking and before the intent arrived."
            },
            [PlayingOperations.LootOpen] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["corpse_not_found"] =
                    "The corpse decayed or was emptied and removed after the bot saw it and before the intent arrived.",
                ["combat_character_dead"] =
                    "A Mob killed the bot while it opened the corpse; a death closes every view."
            },
            [PlayingOperations.LootAll] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["corpse_not_found"] =
                    "The corpse decayed between the open and the loot.",
                ["inventory_full"] =
                    "The fighter's slots or carry capacity are used up by what it looted, so nothing more fits; a player meets the same answer until it sells or drops something.",
                ["combat_character_dead"] =
                    "A Mob killed the bot while it looted; a death closes every view."
            },
            [PlayingOperations.LootClose] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["corpse_not_found"] =
                    "The looted corpse was emptied and removed, or decayed, before the close arrived.",
                ["combat_character_dead"] =
                    "A Mob killed the bot while it closed the corpse; a death closes every view."
            }
        };

    public static bool IsExpected(string operation, string? code)
    {
        return code is not null
            && Table.TryGetValue(operation, out var codes)
            && codes.ContainsKey(code);
    }
}
