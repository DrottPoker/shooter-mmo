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

    // Measurements that only their own script runs, such as tools/Measure-Replication.ps1; CI
    // and tools/Test-Changes.ps1 leave them out.
    public const string Benchmark = "Benchmark";

    public const string Postgres = "Postgres";

    public const string Redis = "Redis";
}
