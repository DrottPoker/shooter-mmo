namespace Mmorpg.GameService.Characters.Contracts;

// Read-only SQL fragments over the characters table for other modules' statements.
// Each argument is a trusted SQL expression from the caller's own statement, never a
// user value. The fragments use their own aliases so they never collide with the
// caller's.
public static class CharactersSql
{
    // A derived table of every character that is not deleted, with the columns other
    // modules may read. Use it in a from or join clause with the caller's own alias.
    public const string ActiveCharacters =
        "(select id, account_id, name from characters where deleted_at is null)";

    // The currency balance of a character.
    public static string CurrencyOf(string characterIdExpression)
    {
        return $"""
            (select characters_sql_currency.currency
             from characters characters_sql_currency
             where characters_sql_currency.id = {characterIdExpression})
            """;
    }

    // The character id when the character exists, deleted or not, otherwise null.
    public static string ExistingIdOrNull(string characterIdExpression)
    {
        return $"""
            (select characters_sql_character.id
             from characters characters_sql_character
             where characters_sql_character.id = {characterIdExpression})
            """;
    }
}
