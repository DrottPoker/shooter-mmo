namespace Mmorpg.SimulationWorker.Realtime;

// The kinds of client actions that GameService must commit, which are refused while it does not
// answer; the refused_intents metric counts them by these names.
internal static class DurableIntentKinds
{
    public const string ItemOperation = "item_operation";
    public const string CorpseInteraction = "corpse_interaction";
    public const string WorldInteraction = "world_interaction";
    public const string QuestIntent = "quest_intent";
    public const string Gathering = "gathering";
    public const string Appearance = "appearance";
}
