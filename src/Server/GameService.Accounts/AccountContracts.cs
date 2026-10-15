namespace Mmorpg.GameService.Accounts;

internal sealed record RegisterAccountRequest(string? Email, string? Username, string? Password);

internal sealed record LoginAccountRequest(string? Login, string? Password);

internal sealed record AuthResponse(
    Guid AccountId,
    string Username,
    Guid SessionId,
    string SessionToken,
    DateTime ExpiresAt);

internal sealed record AccountProfileResponse(
    Guid AccountId,
    string Email,
    string Username,
    DateTime CreatedAt);
