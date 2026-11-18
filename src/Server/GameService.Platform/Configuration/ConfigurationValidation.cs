using System.Diagnostics.CodeAnalysis;
using Mmorpg.Server.Common.Configuration;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Platform.Configuration;

// Shared readers for required GameService settings. Each reader records a
// human-readable error instead of throwing, so one startup failure lists every problem.
public static class ConfigurationValidation
{
    // The placeholders and published values that protect nothing live in PlaceholderSecrets,
    // which SimulationWorker reads too.
    public static IReadOnlyList<string> RepositoryPasswords => PlaceholderSecrets.RepositoryPasswords;

    public static bool IsPlaceholderSecret(string secret)
    {
        return PlaceholderSecrets.IsPlaceholderSecret(secret);
    }

    public static bool IsPlaceholderPassword(string? password)
    {
        return PlaceholderSecrets.IsPlaceholderPassword(password);
    }

    // A service secret that the repository's tooling publishes.
    public static bool IsRepositorySecret(string secret)
    {
        return PlaceholderSecrets.RepositorySecrets.Contains(secret, StringComparer.Ordinal);
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
