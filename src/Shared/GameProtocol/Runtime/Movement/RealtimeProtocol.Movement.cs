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
                && settings.SprintSpeed >= settings.WalkSpeed
                && settings.MinimumX < settings.MaximumX
                && settings.MinimumZ < settings.MaximumZ
                && settings.CharacterHeight >= settings.CharacterRadius * 2f
                && settings.StepHeight < settings.CharacterHeight
                && settings.MaximumSubstepDistance <= settings.CharacterRadius;
        }

        // The move vector is at most one unit long, with room for float rounding.
        private static bool SatisfiesRules(RealtimeMovementInput input)
        {
            return (input.MoveX * input.MoveX) + (input.MoveY * input.MoveY) <= 1.0001f;
        }
    }
}
