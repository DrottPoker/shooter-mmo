using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Mmorpg.SimulationWorker.Config;

// Corpse interaction settings. Bank and Recovery Storage access comes from world actor
// capabilities, see ItemInteractionAccessService.
public sealed record ItemInteractionConfig(
    float CorpseInteractionRadius,
    float CorpseDiscoveryRadius,
    TimeSpan DurableCorpseRefreshInterval)
{
    public static ItemInteractionConfig Empty { get; } = new(
        3f,
        32f,
        TimeSpan.FromSeconds(30));

    internal static ItemInteractionConfig FromConfiguration(
        IConfigurationSection sharedSection,
        ICollection<string> errors)
    {
        var corpseInteractionRadius = ParseFinite(
            sharedSection["CorpseInteractionRadius"] ?? "3",
            $"{sharedSection.Path}:CorpseInteractionRadius",
            errors);
        var corpseDiscoveryRadius = ParseFinite(
            sharedSection["CorpseDiscoveryRadius"] ?? "32",
            $"{sharedSection.Path}:CorpseDiscoveryRadius",
            errors);
        var durableCorpseRefreshSeconds = ParsePositiveInt(
            sharedSection["DurableCorpseRefreshSeconds"] ?? "30",
            $"{sharedSection.Path}:DurableCorpseRefreshSeconds",
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

        if (durableCorpseRefreshSeconds > 300)
        {
            errors.Add(
                $"{sharedSection.Path}:DurableCorpseRefreshSeconds must not exceed 300.");
        }

        return new ItemInteractionConfig(
            corpseInteractionRadius,
            corpseDiscoveryRadius,
            TimeSpan.FromSeconds(Math.Max(1, durableCorpseRefreshSeconds)));
    }

    private static int ParsePositiveInt(
        string? value,
        string key,
        ICollection<string> errors)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            errors.Add($"{key} must be a positive integer.");
            return 1;
        }

        return parsed;
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
}
