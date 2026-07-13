namespace AuthService.Auth;

public static class AccountSessionRevocationReason
{
    public const string Logout = "logout";
    public const string ManualRevoke = "manual_revoke";
    public const string SessionReplaced = "session_replaced";
    public const string LegacyRevocation = "legacy_revocation";
}

public static class AccountSessionErrorCode
{
    public const string SessionReplaced = "account_session_replaced";
}
