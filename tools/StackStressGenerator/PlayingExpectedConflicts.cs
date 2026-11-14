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
    public const string GatherOpen = "gather_open";
    public const string GatherStart = "gather_start";
    public const string GatherClose = "gather_close";
    public const string VendorOpen = "vendor_open";
    public const string VendorSale = "vendor_sale";
    public const string VendorPurchase = "vendor_purchase";
    public const string VendorClose = "vendor_close";
    public const string ItemMove = "item_move";

    // The churn: the leave, from its request to its acceptance, and the way back to a body that
    // still stands or from the saved place, from the first request of the way back, the login or
    // the character list, to the join's acceptance. A rejoin whose place or health check fails
    // is an unexpected rejection with the check's code.
    public const string Leave = "leave";
    public const string RejoinBody = "rejoin_body";
    public const string RejoinSaved = "rejoin_saved";

    public static readonly IReadOnlyList<string> All =
    [
        Equip,
        Teleport,
        Attack,
        ClearTarget,
        LootOpen,
        LootAll,
        LootClose,
        GatherOpen,
        GatherStart,
        GatherClose,
        VendorOpen,
        VendorSale,
        VendorPurchase,
        VendorClose,
        ItemMove,
        Leave,
        RejoinBody,
        RejoinSaved
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
                    "The character's item state changed between the intent and its handling: spent arrows commit in batches without an intent, and a death or a loot moves items too.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses item operations from a dead character."
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
            },
            [PlayingOperations.GatherOpen] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["world_actor_unavailable"] =
                    "Another gatherer took the node's last charge after the bot chose it, and the depleted node waits for its respawn.",
                ["world_interaction_target_changed"] =
                    "The node depleted or respawned, which changes its interaction revision, after the bot saw it and before the intent arrived.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses every world interaction but a close from a dead character."
            },
            [PlayingOperations.GatherStart] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resource_node_depleted"] =
                    "Other gatherers took the node's last charges between the bot's open and its start.",
                ["world_actor_unavailable"] =
                    "The node depleted between the bot's open and its start.",
                ["world_interaction_target_changed"] =
                    "The node depleted between the bot's open and its start, which changes its interaction revision.",
                ["world_interaction_session_invalid"] =
                    "The worker closed the interaction, as it does once the node depletes, before the start arrived.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses every world interaction but a close from a dead character."
            },
            [PlayingOperations.GatherClose] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["world_actor_unavailable"] =
                    "The node depleted before the close arrived; the worker closes that interaction itself.",
                ["world_interaction_target_changed"] =
                    "The node depleted before the close arrived, which changes its interaction revision; the worker closes that interaction itself.",
                ["world_interaction_session_invalid"] =
                    "The worker closed the interaction, as it does once the node depletes, before the close arrived."
            },
            [PlayingOperations.VendorOpen] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses every world interaction but a close from a dead character."
            },
            [PlayingOperations.VendorSale] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_state_conflict"] =
                    "The character's item state changed between the intent and its handling: spent arrows of a bow fighter commit in batches without an intent.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses every world interaction but a close from a dead character."
            },
            [PlayingOperations.VendorPurchase] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_state_conflict"] =
                    "The character's item state changed between the intent and its handling: spent arrows of a bow fighter commit in batches without an intent.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses every world interaction but a close from a dead character."
            },
            [PlayingOperations.VendorClose] = new Dictionary<string, string>(StringComparer.Ordinal),
            [PlayingOperations.ItemMove] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["item_state_conflict"] =
                    "The character's item state changed between the intent and its handling, such as by a vendor trade whose change had not reached the bot yet.",
                ["combat_character_dead"] =
                    "A Mob killed the bot after it sent the intent and before the worker handled it; the worker refuses item operations from a dead character."
            },
            // A bot leaves only joined, alive, out of combat, and without an intent in flight,
            // and the worker accepts the leave of a session that already ended with no body time.
            [PlayingOperations.Leave] = new Dictionary<string, string>(StringComparer.Ordinal),
            // GameService never refuses the way back to the character's own session: it places
            // the ticket on the worker that holds the body, without a capacity check, and once
            // the body is gone it is a normal join from the saved place. A body that a Mob killed
            // before the bot came back is no rejection either: the join enters from the saved
            // place, and the bot judges it as such by the join's own reconnect flag.
            [PlayingOperations.RejoinBody] = new Dictionary<string, string>(StringComparer.Ordinal),
            [PlayingOperations.RejoinSaved] = new Dictionary<string, string>(StringComparer.Ordinal)
        };

    public static bool IsExpected(string operation, string? code)
    {
        return code is not null
            && Table.TryGetValue(operation, out var codes)
            && codes.ContainsKey(code);
    }
}
