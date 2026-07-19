using System.Globalization;
using Microsoft.Extensions.Configuration;
using ShooterMmo.GameSimulation;

namespace SimulationWorker.Config;

public enum ItemServiceKind
{
    Bank = 1,
    RecoveryStorage = 2,
    InsuranceNpc = 3
}

public sealed record ItemServicePointConfig(
    string Id,
    ItemServiceKind Kind,
    float X,
    float Y,
    float Z,
    float Radius);

public sealed record ItemInteractionConfig(
    IReadOnlyList<ItemServicePointConfig> ServicePoints,
    float CorpseInteractionRadius,
    float CorpseDiscoveryRadius)
{
    public static ItemInteractionConfig Empty { get; } = new(
        Array.Empty<ItemServicePointConfig>(),
        3f,
        32f);

    internal static ItemInteractionConfig FromConfiguration(
        IConfigurationSection sharedSection,
        IConfigurationSection worldProfileSection,
        MovementSimulationSettings? movement,
        ICollection<string> errors)
    {
        var points = new List<ItemServicePointConfig>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pointSection in worldProfileSection.GetSection("ServicePoints").GetChildren())
        {
            var prefix = $"{worldProfileSection.Path}:ServicePoints:{pointSection.Key}";
            var id = pointSection["Id"]?.Trim();
            var kindValue = pointSection["Kind"]?.Trim();
            var x = ParseFinite(pointSection["X"], $"{prefix}:X", errors);
            var y = ParseFinite(pointSection["Y"], $"{prefix}:Y", errors);
            var z = ParseFinite(pointSection["Z"], $"{prefix}:Z", errors);
            var radius = ParseFinite(pointSection["Radius"], $"{prefix}:Radius", errors);

            if (!IsValidIdentifier(id))
            {
                errors.Add($"{prefix}:Id must be a valid identifier.");
            }
            else if (!ids.Add(id!))
            {
                errors.Add($"{prefix}:Id must be unique within the worker.");
            }

            if (!TryParseKind(kindValue, out var kind))
            {
                errors.Add(
                    $"{prefix}:Kind must be bank, recovery_storage, or insurance_npc.");
            }

            if (radius <= 0f || radius > 100f)
            {
                errors.Add($"{prefix}:Radius must be greater than 0 and at most 100.");
            }

            if (movement is not null
                && (x < movement.MinimumX
                    || x > movement.MaximumX
                    || z < movement.MinimumZ
                    || z > movement.MaximumZ
                    || y < movement.GroundHeight))
            {
                errors.Add($"{prefix} must be inside the configured movement bounds.");
            }

            if (IsValidIdentifier(id)
                && TryParseKind(kindValue, out kind)
                && radius > 0f
                && radius <= 100f)
            {
                points.Add(new ItemServicePointConfig(id!, kind, x, y, z, radius));
            }
        }

        var corpseInteractionRadius = ParseFinite(
            sharedSection["CorpseInteractionRadius"] ?? "3",
            $"{sharedSection.Path}:CorpseInteractionRadius",
            errors);
        var corpseDiscoveryRadius = ParseFinite(
            sharedSection["CorpseDiscoveryRadius"] ?? "32",
            $"{sharedSection.Path}:CorpseDiscoveryRadius",
            errors);
        if (corpseInteractionRadius <= 0f || corpseInteractionRadius > 20f)
        {
            errors.Add(
                $"{sharedSection.Path}:CorpseInteractionRadius must be greater than 0 and at most 20.");
        }

        if (corpseDiscoveryRadius < corpseInteractionRadius
            || corpseDiscoveryRadius > 1_000f)
        {
            errors.Add(
                $"{sharedSection.Path}:CorpseDiscoveryRadius must be at least the interaction radius and at most 1000.");
        }

        return new ItemInteractionConfig(
            points,
            corpseInteractionRadius,
            corpseDiscoveryRadius);
    }

    private static float ParseFinite(
        string? value,
        string key,
        ICollection<string> errors)
    {
        if (!float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || float.IsNaN(parsed)
            || float.IsInfinity(parsed))
        {
            errors.Add($"{key} must be a finite number.");
            return 0f;
        }

        return parsed;
    }

    private static bool TryParseKind(string? value, out ItemServiceKind kind)
    {
        kind = value switch
        {
            "bank" => ItemServiceKind.Bank,
            "recovery_storage" => ItemServiceKind.RecoveryStorage,
            "insurance_npc" => ItemServiceKind.InsuranceNpc,
            _ => default
        };
        return kind != default;
    }

    private static bool IsValidIdentifier(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= 128
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
