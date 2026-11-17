using System.Diagnostics.CodeAnalysis;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Platform.Configuration;

// Shared readers for required GameService settings. Each reader records a
// human-readable error instead of throwing, so one startup failure lists every problem.
public static class ConfigurationValidation
{
    // The literal passwords of the repository's own Compose files and CI workflow. They are
    // public, so a deployment outside Development must never use one. A guard test keeps the
    // list in step with those files.
    public static IReadOnlyList<string> RepositoryPasswords { get; } =
    [
        "stack_stress_local_only",
        "shooter_mmo_tests_password"
    ];

    // Every secret placeholder in .env.example starts with this prefix.
    public static bool IsPlaceholderSecret(string secret)
    {
        return secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase);
    }

    // A password that protects nothing: empty, a placeholder of .env.example, or a password
    // that the repository publishes.
    public static bool IsPlaceholderPassword(string? password)
    {
        return string.IsNullOrWhiteSpace(password)
            || IsPlaceholderSecret(password)
            || RepositoryPasswords.Contains(password, StringComparer.Ordinal);
    }

    // An optional integer: the default when the key is absent, else a value in the range.
    public static int ReadOptionalIntRange(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        return string.IsNullOrWhiteSpace(configuration[key])
            ? defaultValue
            : ValidateIntRange(configuration, key, minimum, maximum, errors);
    }

    public static string? RequireConnectionString(
        IConfiguration configuration,
        string name,
        ICollection<string> errors)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"ConnectionStrings:{name} is required.");
        }

        return value;
    }

    public static int ValidatePositiveInt(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        var value = configuration[key];
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            errors.Add($"{key} must be a positive integer.");
            return 1;
        }

        return parsed;
    }

    public static int ValidateIntRange(
        IConfiguration configuration,
        string key,
        int minimum,
        int maximum,
        ICollection<string> errors)
    {
        if (!int.TryParse(configuration[key], out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            errors.Add($"{key} must be an integer from {minimum} to {maximum}.");
            return minimum;
        }

        return parsed;
    }

    public static bool ValidateBoolean(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        if (!bool.TryParse(configuration[key], out var parsed))
        {
            errors.Add($"{key} must be true or false.");
        }

        return parsed;
    }

    public static bool IsValidIdentifier([NotNullWhen(true)] string? value)
    {
        return Identifiers.IsServiceIdentifier(value);
    }
}
