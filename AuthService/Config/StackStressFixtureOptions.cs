using Npgsql;

namespace AuthService.Config;

public sealed record StackStressFixtureOptions(bool Enabled, string AuthoritySecret)
{
    public const string SectionName = "StackStressFixtures";
    public const string SecretEnvironmentVariable = "STACK_STRESS_FIXTURE_SECRET";
    public const string AuthorityHeaderName = "X-Stack-Stress-Fixture-Key";

    public static StackStressFixtureOptions FromConfiguration(
        IConfiguration configuration,
        bool isDevelopment,
        string postgresConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue($"{SectionName}:Enabled", false);
        var secret = configuration[SecretEnvironmentVariable]?.Trim() ?? string.Empty;
        var errors = new List<string>();
        if (enabled && !isDevelopment)
        {
            errors.Add($"{SectionName}:Enabled can only be true in the Development environment.");
        }

        if (enabled
            && (secret.Length < 32
                || secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(
                $"{SecretEnvironmentVariable} must contain a non-placeholder secret of at least 32 characters when stack stress fixtures are enabled.");
        }

        if (enabled)
        {
            ValidateDisposableDatabase(postgresConnectionString, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Stack stress fixture configuration is invalid:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        return new StackStressFixtureOptions(enabled, secret);
    }

    private static void ValidateDisposableDatabase(
        string connectionString,
        ICollection<string> errors)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var host = builder.Host?.Trim() ?? string.Empty;
            if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                && !string.Equals(host, "::1", StringComparison.Ordinal))
            {
                errors.Add("Stack stress fixtures require a loopback PostgreSQL host.");
            }

            var database = builder.Database?.Trim() ?? string.Empty;
            if (!database.Contains("stress", StringComparison.OrdinalIgnoreCase)
                && !database.Contains("test", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Stack stress fixtures require a database name containing 'stress' or 'test'.");
            }
        }
        catch (ArgumentException exception)
        {
            errors.Add($"Stack stress fixtures received an invalid PostgreSQL connection string: {exception.Message}");
        }
    }
}
