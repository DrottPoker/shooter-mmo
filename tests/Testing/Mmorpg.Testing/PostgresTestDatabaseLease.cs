namespace Mmorpg.Testing;

// One test's own database. Disposing the lease lets a later test of the same run reuse the
// database name; the database itself stays until that reuse, so a failed test's state can
// still be inspected.
public sealed class PostgresTestDatabaseLease : IAsyncDisposable
{
    private readonly Action release;
    private int released;

    internal PostgresTestDatabaseLease(string connectionString, Action release)
    {
        ConnectionString = connectionString;
        this.release = release;
    }

    // The full connection string, including the password.
    public string ConnectionString { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            release();
        }

        return ValueTask.CompletedTask;
    }
}
