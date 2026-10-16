using System.Text.RegularExpressions;

namespace Mmorpg.Testing;

// Describes the starting state of a leased test database. PostgresTestDatabase builds each
// template once per test run, as an empty public schema followed by Initialize, and copies
// it for every lease. Initialize receives an unpooled connection string, so no connection
// to the template outlives it.
public sealed partial class PostgresTestTemplate
{
    public PostgresTestTemplate(string name, Func<string, Task>? initialize = null)
    {
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException($"The template name '{name}' must match {NamePattern()}.", nameof(name));
        }

        Name = name;
        Initialize = initialize;
    }

    // An empty public schema, as a test that runs migrations itself expects.
    public static PostgresTestTemplate Empty { get; } = new("empty");

    public string Name { get; }

    public Func<string, Task>? Initialize { get; }

    [GeneratedRegex("^[a-z][a-z0-9]{0,15}$")]
    private static partial Regex NamePattern();
}
