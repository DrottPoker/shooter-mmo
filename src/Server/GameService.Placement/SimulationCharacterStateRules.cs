using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Placement;

// The bounds of a character state that a worker sends, which the character_simulation_states
// checks repeat. Health and mana are the combat content bounds; a living character has health.
internal static class SimulationCharacterStateRules
{
    public const string InvalidStateCode = "invalid_character_state";
    public const string InvalidCheckpointCode = "invalid_character_checkpoint";

    // Each state is about 200 bytes of JSON, so a full checkpoint stays far below the request
    // body limit.
    public const int MaximumCheckpointStates = 500;

    public const float MaximumCoordinate = 1_000_000f;
    public const float MaximumYawDegrees = 360f;
    public const int MaximumHealth = 1_000_000;
    public const int MaximumMana = 1_000_000;

    // Null when the state is valid, otherwise what is wrong with it; the subject names the state.
    public static string? Validate(SimulationCharacterStateRequest state, string subject)
    {
        if (state.Sequence <= 0)
        {
            return $"{subject} must have a sequence above 0.";
        }

        if (!IsCoordinate(state.PositionX) || !IsCoordinate(state.PositionY) || !IsCoordinate(state.PositionZ))
        {
            return $"{subject} must have a finite position within {MaximumCoordinate:0} meters of the origin.";
        }

        if (!float.IsFinite(state.YawDegrees) || Math.Abs(state.YawDegrees) > MaximumYawDegrees)
        {
            return $"{subject} must have a finite facing between -{MaximumYawDegrees:0} and {MaximumYawDegrees:0} degrees.";
        }

        if (state.Health is < 1 or > MaximumHealth)
        {
            return $"{subject} must have a health between 1 and {MaximumHealth}.";
        }

        if (state.Mana is < 0 or > MaximumMana)
        {
            return $"{subject} must have a mana between 0 and {MaximumMana}.";
        }

        return null;
    }

    public static ServiceResult<T> Invalid<T>(string message)
    {
        return ServiceResult<T>.BadRequest(InvalidStateCode, message);
    }

    private static bool IsCoordinate(float value)
    {
        return float.IsFinite(value) && Math.Abs(value) <= MaximumCoordinate;
    }
}
