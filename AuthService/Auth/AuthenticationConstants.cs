namespace AuthService.Auth;

public static class AuthenticationConstants
{
    public const string AccountSessionScheme = "AccountSession";
    public const string WorldServerScheme = "WorldServer";
    public const string AccountSessionPolicy = "AccountSession";
    public const string WorldServerPolicy = "WorldServer";
    public const string AccountIdClaim = "account_id";
    public const string SessionIdClaim = "session_id";
    public const string WorldIdClaim = "world_id";
    public const string WorldServerIdHeader = "X-World-Server-ID";
    public const string WorldServerSecretHeader = "X-World-Server-Secret";
}
