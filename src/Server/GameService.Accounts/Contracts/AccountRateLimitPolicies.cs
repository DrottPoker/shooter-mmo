namespace Mmorpg.GameService.Accounts.Contracts;

// The per-account limits of the account-session routes, as docs/SERVICE_FEATURES.md describes
// under Rate Limits. A route names one of them with RequireRateLimiting.
public static class AccountRateLimitPolicies
{
    // A route that creates work or state for the account, such as a join or a new character.
    // Each such route counts in a window of its own per account.
    public const string Action = "account-action";

    // A route that reads the account's state. All of them share one window per account.
    public const string Read = "account-read";
}
