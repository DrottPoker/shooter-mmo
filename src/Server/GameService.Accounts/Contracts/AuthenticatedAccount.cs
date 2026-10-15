namespace Mmorpg.GameService.Accounts.Contracts;

public sealed record AuthenticatedAccount(Guid AccountId, string Username, Guid SessionId);
