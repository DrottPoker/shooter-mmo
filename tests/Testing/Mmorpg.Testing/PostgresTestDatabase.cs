using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Mmorpg.Testing;

// Gives every test assembly its own PostgreSQL database next to the configured base
// database, so test projects run in parallel against one server without sharing schema,
// rows, or advisory locks. Databases are created on demand and never dropped.
public static partial class PostgresTestDatabase
{
    public const string ConnectionStringVariable = "MMORPG_TEST_POSTGRES";

    // Session advisory lock that marks a database as used by one test run. Production code
    // never takes this key.
    private const long RunLockKey = 7104202610150001;

    private const int CreateAttempts = 5;

    // One database per test process, and a test process runs exactly one test assembly.
    // The cached result keeps the run lock connection open until the process exits.
    private static readonly Lazy<Task<ProvisionedDatabase>> Current = new(ProvisionAsync);

    // The connection string of this test assembly's database, created on first use.
    public static async Task<string> GetConnectionStringAsync()
    {
        return (await Current.Value).ConnectionString;
    }

    // Tests reset whole schemas, so they refuse any database that is not named as a test
    // database.
    public static void EnsureDedicatedTestDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Database)
            || !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The PostgreSQL integration connection must target a database whose name contains 'test'.");
        }
    }

    public static async Task ResetPublicSchemaAsync(NpgsqlDataSource dataSource)
    {
        EnsureDedicatedTestDatabase(dataSource.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "drop schema if exists public cascade; create schema public;");

        await command.ExecuteNonQueryAsync();
    }

    // <base>_<project>: the assembly name without the Mmorpg. prefix and the .Tests suffix,
    // lowercased, with dots mapped to underscores.
    public static string DatabaseNameFor(string baseDatabase, string testAssemblyName)
    {
        var project = testAssemblyName;
        if (project.StartsWith("Mmorpg.", StringComparison.Ordinal))
        {
            project = project["Mmorpg.".Length..];
        }

        if (project.EndsWith(".Tests", StringComparison.Ordinal))
        {
            project = project[..^".Tests".Length];
        }

        var name = $"{baseDatabase}_{project.ToLowerInvariant().Replace('.', '_')}";
        if (!DatabaseNamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"The test database name '{name}' derived for '{testAssemblyName}' must match {DatabaseNamePattern()}.");
        }

        return name;
    }

    private static async Task<ProvisionedDatabase> ProvisionAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to a dedicated PostgreSQL test database.");
        }

        EnsureDedicatedTestDatabase(baseConnectionString);
        var testAssemblyName = TestAssemblyName();
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString);
        builder.Database = DatabaseNameFor(builder.Database!, testAssemblyName);
        var connectionString = builder.ConnectionString;
        EnsureDedicatedTestDatabase(connectionString);

        await CreateDatabaseIfMissingAsync(baseConnectionString, builder.Database);
        var runLock = await AcquireRunLockAsync(connectionString, builder.Database, testAssemblyName);
        return new ProvisionedDatabase(connectionString, runLock);
    }

    private static string TestAssemblyName()
    {
        var name = TestContext.Current.TestAssembly?.AssemblyName
            ?? Assembly.GetEntryAssembly()?.FullName
            ?? throw new InvalidOperationException("The running test assembly is unknown.");
        return new AssemblyName(name).Name
            ?? throw new InvalidOperationException($"The test assembly name '{name}' has no simple name.");
    }

    private static async Task CreateDatabaseIfMissingAsync(string baseConnectionString, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        for (var attempt = 1; ; attempt++)
        {
            await using (var exists = new NpgsqlCommand(
                "select 1 from pg_database where datname = $1",
                connection))
            {
                exists.Parameters.Add(new NpgsqlParameter { Value = database });
                if (await exists.ExecuteScalarAsync() is not null)
                {
                    return;
                }
            }

            try
            {
                // The name matched DatabaseNamePattern, so quoting it is safe.
                await using var create = new NpgsqlCommand($"create database \"{database}\"", connection);
                await create.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.DuplicateDatabase
                or PostgresErrorCodes.UniqueViolation)
            {
                // Another test process created it first.
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse
                && attempt < CreateAttempts)
            {
                // The template database was briefly in use by another session.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new InvalidOperationException(
                    $"The test role cannot create the test database '{database}'. Grant it CREATEDB or create the database beforehand.",
                    exception);
            }
        }
    }

    // The lock lives on an unpooled session that stays open, so it is released exactly when
    // the test process exits.
    private static async Task<NpgsqlConnection> AcquireRunLockAsync(
        string connectionString,
        string database,
        string testAssemblyName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("select pg_try_advisory_lock($1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = RunLockKey });
            if (await command.ExecuteScalarAsync() is not true)
            {
                throw new InvalidOperationException(
                    $"Another run of {testAssemblyName} is using test database '{database}'. Run one test run per project and server at a time.");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    [GeneratedRegex("^[a-z0-9_]{1,63}$")]
    private static partial Regex DatabaseNamePattern();

    private sealed record ProvisionedDatabase(string ConnectionString, NpgsqlConnection RunLock);
}
