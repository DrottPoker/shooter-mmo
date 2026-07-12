namespace AuthService.Auth;

public sealed record RegisterAccountRequest(string? Email, string? Username, string? Password);

public sealed record LoginAccountRequest(string? Login, string? Password);

public sealed record AuthResponse(
    Guid AccountId,
    string Username,
    Guid SessionId,
    string SessionToken,
    DateTime ExpiresAt);

public sealed record AccountProfileResponse(
    Guid AccountId,
    string Email,
    string Username,
    DateTime CreatedAt);
