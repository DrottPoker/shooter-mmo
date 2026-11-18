namespace Mmorpg.Server.Common.Configuration;

// The passwords and secrets that protect nothing: the placeholders of .env.example and the
// literal values that the repository publishes. GameService and SimulationWorker refuse them
// outside the Development environment, so the rules live here once.
public static class PlaceholderSecrets
{
    // The literal passwords of the repository's own Compose files and CI workflow. They are
    // public, so a deployment outside Development must never use one. A guard test keeps the
    // list in step with those files.
    public static IReadOnlyList<string> RepositoryPasswords { get; } =
    [
        "stack_stress_local_only",
        "shooter_mmo_tests_password"
    ];

    // The literal worker secrets of the repository's load tooling. A guard test keeps the list
    // in step with the tool.
    public static IReadOnlyList<string> RepositorySecrets { get; } =
    [
        "stack-stress-local-worker-secret-2026"
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

    // A service secret that protects nothing: a placeholder of .env.example or a secret that
    // the repository's tooling publishes. Length rules stay with each setting.
    public static bool IsPlaceholderServiceSecret(string secret)
    {
        return IsPlaceholderSecret(secret)
            || RepositorySecrets.Contains(secret, StringComparer.Ordinal);
    }
}
