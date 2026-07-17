using Microsoft.Extensions.Configuration;

namespace SimulationWorker.Config;

public sealed record DevelopmentItemInteractionOptions(
    bool GlobalBankAndRecoveryAccess)
{
    public const string SectionName = "DevelopmentItemInteractions";

    public static DevelopmentItemInteractionOptions Disabled { get; } = new(false);

    public static DevelopmentItemInteractionOptions FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration
            .GetSection(SectionName)
            .GetValue("GlobalBankAndRecoveryAccess", false);
        if (enabled && !isDevelopment)
        {
            throw new InvalidOperationException(
                $"{SectionName}:GlobalBankAndRecoveryAccess can only be true "
                + "in the Development environment.");
        }

        return new DevelopmentItemInteractionOptions(enabled);
    }
}
