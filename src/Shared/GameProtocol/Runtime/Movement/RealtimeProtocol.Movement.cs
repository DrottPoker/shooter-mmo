namespace Mmorpg.GameProtocol
{
    // Cross-field movement rules. The schema generates the models and codecs, and the
    // generated validation calls these after every field rule passed.
    public static partial class RealtimeProtocol
    {
        private static bool SatisfiesRules(RealtimeMovementSettings settings)
        {
            return settings.SnapshotRateHz <= settings.TickRateHz
                && settings.TickRateHz % settings.SnapshotRateHz == 0
                && settings.MinimumX < settings.MaximumX
                && settings.MinimumZ < settings.MaximumZ
                && settings.CharacterHeight >= settings.CharacterRadius * 2f
                && settings.StepHeight < settings.CharacterHeight
                && settings.MaximumSubstepDistance <= settings.CharacterRadius;
        }

        private static bool SatisfiesRules(RealtimeMovementPathProgress progress)
        {
            return progress.PathId != 0
                || (progress.NextWaypointIndex == 0 && progress.BlockedTicks == 0);
        }

        // A path travels only with the input that follows it, so the worker never has to
        // match a path to an input from another batch.
        private static bool SatisfiesRules(RealtimeMovementInputBatch batch)
        {
            return batch.Path == null
                || batch.Path.PathId == batch.Inputs[batch.Inputs.Length - 1].PathId;
        }
    }
}
