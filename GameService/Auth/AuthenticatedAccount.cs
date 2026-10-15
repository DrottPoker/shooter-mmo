namespace Mmorpg.GameService.Auth;

public sealed record AuthenticatedAccount(Guid AccountId, string Username, Guid SessionId);
