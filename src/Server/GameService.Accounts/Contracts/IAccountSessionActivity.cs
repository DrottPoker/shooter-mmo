using Npgsql;

namespace Mmorpg.GameService.Accounts.Contracts;

// Counts the play of a live simulation session as activity of its account session, so the account
// session of a player in the World never ends while idle. Exactly one implementation is registered.
public interface IAccountSessionActivity
{
    // Moves the ends of the given active account sessions to the idle lifetime from now, never past
    // their maximum lifetime, in one statement outside a transaction on the caller's connection. A
    // session whose end would move by an hour or less is not written. It locks the sessions it
    // extends in ascending id order and nothing else, so the caller must hold no lock that a login
    // takes after its account sessions. Returns how many it extended.
    Task<int> ExtendAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<Guid> accountSessionIds,
        CancellationToken cancellationToken);
}
