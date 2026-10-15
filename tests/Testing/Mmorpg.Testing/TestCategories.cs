namespace Mmorpg.Testing;

// Trait names and values for test filters, for example --filter "Category!=Integration".
public static class TestCategories
{
    public const string Category = "Category";

    public const string Infrastructure = "Infrastructure";

    // Needs PostgreSQL or Redis; the test is skipped while its variable is unset.
    public const string Integration = "Integration";

    // Timing budgets that are sensitive to machine load.
    public const string Load = "Load";

    public const string Postgres = "Postgres";

    public const string Redis = "Redis";
}
