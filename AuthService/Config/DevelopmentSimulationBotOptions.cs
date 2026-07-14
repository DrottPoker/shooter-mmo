namespace AuthService.Config;

public sealed record DevelopmentSimulationBotOptions(
    bool Enabled,
    string AuthoritySecret,
    int MaximumActiveBots,
    int ReservedPlayerSlots,
    TimeSpan JoinTicketLifetime,
    TimeSpan SessionLeaseLifetime,
    string CharacterNamePrefix)
{
    public const string SectionName = "DevelopmentSimulationBots";
    public const string SecretEnvironmentVariable = "ACTIVE_SIMULATION_BOTS_SECRET";
    public const string AuthorityHeaderName = "X-Active-Simulation-Bots-Key";

    public static DevelopmentSimulationBotOptions FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", false);
        var secret = configuration[SecretEnvironmentVariable] ?? string.Empty;
        var maximumActiveBots = section.GetValue("MaximumActiveBots", 200);
        var reservedPlayerSlots = section.GetValue("ReservedPlayerSlots", 8);
        var joinTicketLifetimeSeconds = section.GetValue("JoinTicketLifetimeSeconds", 30);
        var sessionLeaseLifetimeSeconds = section.GetValue("SessionLeaseLifetimeSeconds", 30);
        var characterNamePrefix = section.GetValue("CharacterNamePrefix", "Active Bot")?.Trim()
            ?? string.Empty;

        var errors = new List<string>();
        if (enabled && !isDevelopment)
        {
            errors.Add($"{SectionName}:Enabled can only be true in the Development environment.");
        }

        if (enabled
            && (secret.Length < 32
                || secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{SecretEnvironmentVariable} must contain a non-placeholder secret of at least 32 characters when development simulation bots are enabled.");
        }

        if (maximumActiveBots is < 1 or > 10_000)
        {
            errors.Add($"{SectionName}:MaximumActiveBots must be between 1 and 10000.");
        }

        if (reservedPlayerSlots is < 0 or > 10_000)
        {
            errors.Add($"{SectionName}:ReservedPlayerSlots must be between 0 and 10000.");
        }

        if (joinTicketLifetimeSeconds is < 5 or > 300)
        {
            errors.Add($"{SectionName}:JoinTicketLifetimeSeconds must be between 5 and 300.");
        }

        if (sessionLeaseLifetimeSeconds is < 10 or > 300)
        {
            errors.Add($"{SectionName}:SessionLeaseLifetimeSeconds must be between 10 and 300.");
        }

        if (characterNamePrefix.Length is < 1 or > 32)
        {
            errors.Add($"{SectionName}:CharacterNamePrefix must contain between 1 and 32 characters.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Development simulation bot configuration is invalid:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new DevelopmentSimulationBotOptions(
            enabled,
            secret,
            maximumActiveBots,
            reservedPlayerSlots,
            TimeSpan.FromSeconds(joinTicketLifetimeSeconds),
            TimeSpan.FromSeconds(sessionLeaseLifetimeSeconds),
            characterNamePrefix);
    }
}
