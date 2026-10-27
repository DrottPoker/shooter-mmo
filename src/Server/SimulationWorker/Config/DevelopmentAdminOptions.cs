using Microsoft.Extensions.Configuration;

namespace Mmorpg.SimulationWorker.Config;

// Whether every player may use the development admin tools of the Admin menu: the walk speed,
// a teleport, full health, and invulnerability. Only the Development environment allows them.
public sealed record DevelopmentAdminOptions(bool Enabled)
{
    public const string SectionName = "DevelopmentAdmin";

    public static DevelopmentAdminOptions Disabled { get; } = new(false);

    public static DevelopmentAdminOptions FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration
            .GetSection(SectionName)
            .GetValue("Enabled", false);
        if (enabled && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Enabled can only be true in the Development environment.");
        }

        return new DevelopmentAdminOptions(enabled);
    }
}
