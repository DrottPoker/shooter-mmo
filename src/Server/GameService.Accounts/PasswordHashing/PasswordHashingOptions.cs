using Mmorpg.GameService.Platform.Configuration;

namespace Mmorpg.GameService.Accounts.PasswordHashing;

// The password hashing lane: how many BCrypt hashes run at once and how long a request may
// wait for its turn before it is refused with auth_busy.
public sealed record PasswordHashingOptions(int MaxConcurrency, TimeSpan MaxWait)
{
    public const string MaxConcurrencyKey = "Auth:PasswordHashing:MaxConcurrency";
    public const string MaxWaitSecondsKey = "Auth:PasswordHashing:MaxWaitSeconds";
    public const int MaximumConcurrencyLimit = 256;
    public const int DefaultMaxWaitSeconds = 10;

    // The longest wait is part of the login contract: a client gives a login this many seconds
    // on top of its request timeout (ApiClient.AuthenticationQueueSeconds in the Unity client),
    // so a server must not be configured to queue longer.
    public const int MaximumWaitSecondsLimit = 10;

    // Half of the logical processors, so hashing leaves the other half to everything else.
    public static int DefaultMaxConcurrency => Math.Max(1, Environment.ProcessorCount / 2);

    public static PasswordHashingOptions Default { get; } =
        new(DefaultMaxConcurrency, TimeSpan.FromSeconds(DefaultMaxWaitSeconds));

    internal static PasswordHashingOptions Read(IConfiguration configuration, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(errors);

        var maxConcurrency = configuration[MaxConcurrencyKey] is null
            ? DefaultMaxConcurrency
            : ConfigurationValidation.ValidateIntRange(
                configuration,
                MaxConcurrencyKey,
                1,
                MaximumConcurrencyLimit,
                errors);
        var maxWaitSeconds = configuration[MaxWaitSecondsKey] is null
            ? DefaultMaxWaitSeconds
            : ConfigurationValidation.ValidateIntRange(
                configuration,
                MaxWaitSecondsKey,
                1,
                MaximumWaitSecondsLimit,
                errors);
        return new PasswordHashingOptions(maxConcurrency, TimeSpan.FromSeconds(maxWaitSeconds));
    }
}
