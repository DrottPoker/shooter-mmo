using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Mmorpg.Testing;

// Gives every test its own PostgreSQL database next to the configured base database. A test
// run builds each template database once and copies it for every lease, so tests run in
// parallel without sharing schema, rows, or advisory locks, and no test repeats migrations.
// For the base database <base> and the test assembly's <project> the databases are:
//   <base>_<project>             holds the run lock and nothing else
//   <base>_<project>_tpl_<name>  one per template, rebuilt by every run
//   <base>_<project>_<n>         leased test databases, recreated from a template per lease
// Databases are created on demand and never dropped at the end of a run.
public static partial class PostgresTestDatabase
{
    public const string ConnectionStringVariable = "MMORPG_TEST_POSTGRES";

    // Session advisory lock that marks a test assembly's databases as used by one test run.
    // Production code never takes this key.
    private const long RunLockKey = 7104202610150001;

    private const int Attempts = 5;

    // One run per test process, and a test process runs exactly one test assembly. The
    // cached run keeps the run lock connection open until the process exits.
    private static readonly Lazy<Task<TestRun>> Current = new(StartRunAsync);

    // A fresh copy of the template for one test. Dispose the lease when the test ends.
    public static async Task<PostgresTestDatabaseLease> LeaseAsync(PostgresTestTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var run = await Current.Value;
        return await run.LeaseAsync(template);
    }

    // Tests reset and copy whole databases, so they refuse any database that is not named
    // as a test database.
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

        return EnsureValidDatabaseName(
            $"{baseDatabase}_{project.ToLowerInvariant().Replace('.', '_')}",
            testAssemblyName);
    }

    private static string EnsureValidDatabaseName(string name, string testAssemblyName)
    {
        if (!DatabaseNamePattern().IsMatch(name))
        {
            throw new InvalidOperationException(
                $"The test database name '{name}' derived for '{testAssemblyName}' must match {DatabaseNamePattern()}.");
        }

        return name;
    }

    private static async Task<TestRun> StartRunAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionStringVariable} to a dedicated PostgreSQL test database.");
        }

        EnsureDedicatedTestDatabase(baseConnectionString);
        var testAssemblyName = TestAssemblyName();
        var baseDatabase = new NpgsqlConnectionStringBuilder(baseConnectionString).Database!;
        var prefix = DatabaseNameFor(baseDatabase, testAssemblyName);

        // CREATE and DROP DATABASE run on the base database, which tests never use.
        var admin = NpgsqlDataSource.Create(baseConnectionString);
        try
        {
            await CreateDatabaseIfMissingAsync(admin, prefix);
            var runLock = await AcquireRunLockAsync(
                ConnectionStringFor(baseConnectionString, prefix, pooling: false),
                prefix,
                testAssemblyName);
            return new TestRun(admin, baseConnectionString, prefix, testAssemblyName, runLock);
        }
        catch
        {
            await admin.DisposeAsync();
            throw;
        }
    }

    private static string TestAssemblyName()
    {
        var name = TestContext.Current.TestAssembly?.AssemblyName
            ?? Assembly.GetEntryAssembly()?.FullName
            ?? throw new InvalidOperationException("The running test assembly is unknown.");
        return new AssemblyName(name).Name
            ?? throw new InvalidOperationException($"The test assembly name '{name}' has no simple name.");
    }

    private static string ConnectionStringFor(string baseConnectionString, string database, bool pooling)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = database };
        if (!pooling)
        {
            builder.Pooling = false;
        }

        var connectionString = builder.ConnectionString;
        EnsureDedicatedTestDatabase(connectionString);
        return connectionString;
    }

    private static async Task CreateDatabaseIfMissingAsync(NpgsqlDataSource admin, string database)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using (var exists = admin.CreateCommand("select 1 from pg_database where datname = $1"))
            {
                exists.Parameters.Add(new NpgsqlParameter { Value = database });
                if (await exists.ExecuteScalarAsync() is not null)
                {
                    return;
                }
            }

            try
            {
                await CreateDatabaseAsync(admin, database, template: null);
                return;
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.DuplicateDatabase
                or PostgresErrorCodes.UniqueViolation)
            {
                // Another test process created it first.
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse
                && attempt < Attempts)
            {
                // The template database was briefly in use by another session.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    // Drops any earlier copy, including connections a previous test left open, and copies the
    // template. A null template copies the server's default template.
    private static async Task RecreateDatabaseAsync(NpgsqlDataSource admin, string database, string? template)
    {
        // The names matched DatabaseNamePattern, so quoting them is safe. CREATE and DROP
        // DATABASE cannot share one command, because a multi-statement command is a transaction.
        await using (var drop = admin.CreateCommand($"drop database if exists \"{database}\" with (force)"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateDatabaseAsync(admin, database, template);
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse
                && attempt < Attempts)
            {
                // A session on the template was still closing.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }

    private static async Task CreateDatabaseAsync(NpgsqlDataSource admin, string database, string? template)
    {
        var sql = template is null
            ? $"create database \"{database}\""
            : $"create database \"{database}\" template \"{template}\"";
        await using var create = admin.CreateCommand(sql);
        try
        {
            await create.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                $"The test role cannot create the test database '{database}'. Grant it CREATEDB.",
                exception);
        }
    }

    // The empty starting point of every template: the public schema recreated by the test
    // role, exactly as a test that resets its own schema would see it.
    private static async Task ResetPublicSchemaAsync(string connectionString)
    {
        EnsureDedicatedTestDatabase(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand(
            "drop schema if exists public cascade; create schema public;");
        await command.ExecuteNonQueryAsync();
    }

    // The lock lives on an unpooled session that stays open, so it is released exactly when
    // the test process exits.
    private static async Task<NpgsqlConnection> AcquireRunLockAsync(
        string connectionString,
        string database,
        string testAssemblyName)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("select pg_try_advisory_lock($1)", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = RunLockKey });
            if (await command.ExecuteScalarAsync() is not true)
            {
                throw new InvalidOperationException(
                    $"Another run of {testAssemblyName} is using the test databases '{database}_*'. Run one test run per project and server at a time.");
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

    private sealed class TestRun(
        NpgsqlDataSource admin,
        string baseConnectionString,
        string prefix,
        string testAssemblyName,
        NpgsqlConnection runLock)
    {
        private readonly Lock gate = new();
        private readonly Dictionary<string, (PostgresTestTemplate Template, Lazy<Task<string>> Database)> templates =
            new(StringComparer.Ordinal);

        // Released numbers are reused lowest first, so a run needs only as many databases as
        // tests ran at the same time.
        private readonly SortedSet<int> freeNumbers = [];
        private int highestNumber;

        // Referenced so the run lock session stays open for the whole process.
        public NpgsqlConnection RunLock { get; } = runLock;

        public async Task<PostgresTestDatabaseLease> LeaseAsync(PostgresTestTemplate template)
        {
            var templateDatabase = await TemplateDatabaseAsync(template);
            var number = TakeNumber();
            try
            {
                var database = EnsureValidDatabaseName($"{prefix}_{number}", testAssemblyName);
                await RecreateDatabaseAsync(admin, database, templateDatabase);
                return new PostgresTestDatabaseLease(
                    ConnectionStringFor(baseConnectionString, database, pooling: true),
                    () => ReturnNumber(number));
            }
            catch
            {
                ReturnNumber(number);
                throw;
            }
        }

        private Task<string> TemplateDatabaseAsync(PostgresTestTemplate template)
        {
            Lazy<Task<string>> database;
            lock (gate)
            {
                if (!templates.TryGetValue(template.Name, out var entry))
                {
                    entry = (template, new Lazy<Task<string>>(() => BuildTemplateAsync(template)));
                    templates.Add(template.Name, entry);
                }
                else if (!ReferenceEquals(entry.Template, template))
                {
                    throw new InvalidOperationException(
                        $"Two different PostgreSQL test templates are named '{template.Name}'.");
                }

                database = entry.Database;
            }

            // The first caller starts the build; every later caller awaits the same task.
            return database.Value;
        }

        private async Task<string> BuildTemplateAsync(PostgresTestTemplate template)
        {
            var database = EnsureValidDatabaseName($"{prefix}_tpl_{template.Name}", testAssemblyName);
            await RecreateDatabaseAsync(admin, database, template: null);
            var connectionString = ConnectionStringFor(baseConnectionString, database, pooling: false);
            await ResetPublicSchemaAsync(connectionString);
            if (template.Initialize is not null)
            {
                await template.Initialize(connectionString);
            }

            return database;
        }

        private int TakeNumber()
        {
            lock (gate)
            {
                if (freeNumbers.Count > 0)
                {
                    var number = freeNumbers.Min;
                    freeNumbers.Remove(number);
                    return number;
                }

                return ++highestNumber;
            }
        }

        private void ReturnNumber(int number)
        {
            lock (gate)
            {
                freeNumbers.Add(number);
            }
        }
    }
}
