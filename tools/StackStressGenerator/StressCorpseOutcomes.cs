namespace Mmorpg.Tools.StackStressGenerator;

// Classifies corpse interaction rejections that concurrent bots legitimately cause, so
// they stay visible as expected conflicts instead of failing the run.
public static class StressCorpseOutcomes
{
    private static readonly HashSet<string> ExpectedConflictCodes = new(StringComparer.Ordinal)
    {
        "item_already_looted",
        "item_quantity_changed",
        "item_state_conflict",
        "item_slot_occupied",
        "item_stack_limit_exceeded",
        "corpse_view_not_open"
    };

    public static bool IsExpectedRejection(StressWorkloadProfile workload, string? code)
    {
        if (code is null)
        {
            return false;
        }

        // Mixed gameplay moves bots that all start crowded on the corpse, so the
        // authoritative range check can correctly reject a bot that drifted past the
        // interaction radius. Stationary loot hotspot bots must never be out of range.
        return ExpectedConflictCodes.Contains(code)
            || (workload == StressWorkloadProfile.MixedGameplay
                && string.Equals(code, "corpse_out_of_range", StringComparison.Ordinal));
    }
}
