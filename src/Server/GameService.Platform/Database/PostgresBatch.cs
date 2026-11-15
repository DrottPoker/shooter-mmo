using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace Mmorpg.GameService.Platform.Database;

// Statements that travel to PostgreSQL together in one round trip, in the caller's transaction.
// The owner of each statement adds it through its own code and reads its own result once the
// batch ran, so a statement keeps its owner while it shares the round trip. PostgreSQL still
// runs every statement on its own, in the order it was added, each with its own snapshot in
// READ COMMITTED and with the transaction's statement and lock timeouts. A batch runs whole, so
// a statement belongs into it only when no earlier statement's result decides whether it may run
// or what its parameters are.
public sealed class PostgresBatch
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ParameterProperties = new();

    // The DbType that Dapper gives a parameter of each type, so a statement binds its
    // parameters in a batch exactly as it does in a Dapper command.
    private static readonly IReadOnlyDictionary<Type, DbType> DbTypes = new Dictionary<Type, DbType>
    {
        [typeof(byte)] = DbType.Byte,
        [typeof(sbyte)] = DbType.SByte,
        [typeof(short)] = DbType.Int16,
        [typeof(ushort)] = DbType.UInt16,
        [typeof(int)] = DbType.Int32,
        [typeof(uint)] = DbType.UInt32,
        [typeof(long)] = DbType.Int64,
        [typeof(ulong)] = DbType.UInt64,
        [typeof(float)] = DbType.Single,
        [typeof(double)] = DbType.Double,
        [typeof(decimal)] = DbType.Decimal,
        [typeof(bool)] = DbType.Boolean,
        [typeof(string)] = DbType.String,
        [typeof(char)] = DbType.StringFixedLength,
        [typeof(Guid)] = DbType.Guid,
        [typeof(DateTime)] = DbType.DateTime,
        [typeof(DateTimeOffset)] = DbType.DateTimeOffset,
        [typeof(TimeSpan)] = DbType.Time,
        [typeof(byte[])] = DbType.Binary
    };

    private readonly List<Statement> pending = [];

    public PostgresBatch(PostgresTransactionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
    }

    public PostgresTransactionScope Scope { get; }

    // The statements added since the batch last ran.
    public int Count => pending.Count;

    // A statement that returns no rows. Its result is the number of rows it changed.
    public PostgresBatchResult<int> Execute(string sql, object? parameters = null)
    {
        var statement = Add(sql, parameters, read: null);
        return new PostgresBatchResult<int>(() => statement.HasValue, () => (int)statement.Value!);
    }

    // A statement whose rows map to T as Dapper maps them. A select whose rows the caller does
    // not need is added here too and its result left unread.
    public PostgresBatchResult<IReadOnlyList<T>> Query<T>(string sql, object? parameters = null)
    {
        var statement = Add(sql, parameters, ReadAllAsync<T>);
        return new PostgresBatchResult<IReadOnlyList<T>>(
            () => statement.HasValue,
            () => (IReadOnlyList<T>)statement.Value!);
    }

    // A statement that returns at most one row, read as Dapper's QuerySingleOrDefault reads it.
    public PostgresBatchResult<T?> QuerySingleOrDefault<T>(string sql, object? parameters = null)
    {
        var statement = Add(sql, parameters, ReadSingleOrDefaultAsync<T>);
        return new PostgresBatchResult<T?>(() => statement.HasValue, () => (T?)statement.Value);
    }

    // Sends every added statement in one round trip and reads the results in order. When a
    // statement fails, PostgreSQL skips the statements after it, and this method throws the
    // statement's error; the rows that were read before it keep their results, so a caller can
    // still let an earlier check decide. The batch is empty afterwards and takes new statements.
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var statements = pending.ToArray();
        pending.Clear();
        await using var batch = new NpgsqlBatch(Scope.Connection, Scope.Transaction);
        foreach (var statement in statements)
        {
            batch.BatchCommands.Add(statement.CreateCommand());
        }

        await ReadResultsAsync(batch, statements, cancellationToken);

        // The counts of changed rows are known once every statement completed.
        for (var index = 0; index < statements.Length; index++)
        {
            if (!statements[index].ReturnsRows)
            {
                statements[index].Complete(batch.BatchCommands[index].RecordsAffected);
            }
        }
    }

    private Statement Add(
        string sql,
        object? parameters,
        Func<DbDataReader, CancellationToken, Task<object?>>? read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var statement = new Statement(sql, parameters, read);
        pending.Add(statement);
        return statement;
    }

    private static async Task ReadResultsAsync(
        NpgsqlBatch batch,
        Statement[] statements,
        CancellationToken cancellationToken)
    {
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var positioned = true;
        foreach (var statement in statements.Where(statement => statement.ReturnsRows))
        {
            // The reader starts on the first result set; statements without rows have none.
            if ((!positioned && !await reader.NextResultAsync(cancellationToken)) || reader.FieldCount == 0)
            {
                throw new InvalidOperationException("A batch statement added as a query returned no rows.");
            }

            positioned = false;
            await statement.ReadAsync(reader, cancellationToken);
        }

        if (positioned ? reader.FieldCount > 0 : await reader.NextResultAsync(cancellationToken))
        {
            throw new InvalidOperationException("A batch statement added without rows returned rows.");
        }
    }

    private static async Task<object?> ReadAllAsync<T>(DbDataReader reader, CancellationToken cancellationToken)
    {
        var parse = reader.GetRowParser(typeof(T));
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ValueOf<T>(parse(reader)));
        }

        return rows;
    }

    private static async Task<object?> ReadSingleOrDefaultAsync<T>(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        var parse = reader.GetRowParser(typeof(T));
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default(T);
        }

        var row = ValueOf<T>(parse(reader));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Sequence contains more than one element");
        }

        return row;
    }

    // A parsed row as Dapper's queries return it: a single column converts to T when its type
    // differs, as an int column does to a long.
    private static T ValueOf<T>(object? value)
    {
        if (value is T typed)
        {
            return typed;
        }

        if (value is null or DBNull)
        {
            return !typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null
                ? default!
                : throw new InvalidCastException($"A null value cannot be read as {typeof(T).Name}.");
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static NpgsqlParameter CreateParameter(PropertyInfo property, object parameters)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var value = property.GetValue(parameters);
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
            value = value is null ? null : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        var parameter = new NpgsqlParameter
        {
            ParameterName = property.Name,
            Value = value ?? DBNull.Value
        };
        if (DbTypes.TryGetValue(type, out var dbType))
        {
            parameter.DbType = dbType;
        }

        return parameter;
    }

    // Like Dapper, a statement gets only the parameters that its text names.
    private static bool IsReferenced(string sql, string name)
    {
        return Regex.IsMatch(
            sql,
            $@"@{Regex.Escape(name)}(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed class Statement(
        string sql,
        object? parameters,
        Func<DbDataReader, CancellationToken, Task<object?>>? read)
    {
        public bool ReturnsRows => read is not null;

        public bool HasValue { get; private set; }

        public object? Value { get; private set; }

        public NpgsqlBatchCommand CreateCommand()
        {
            // Npgsql sends a command's statement without its closing semicolon, but sends the
            // text of a batch statement without parameters as written, so the batch drops it.
            var command = new NpgsqlBatchCommand(sql.TrimEnd().TrimEnd(';'));
            if (parameters is null)
            {
                return command;
            }

            var properties = ParameterProperties.GetOrAdd(
                parameters.GetType(),
                type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetIndexParameters().Length == 0)
                    .ToArray());
            foreach (var property in properties.Where(property => IsReferenced(sql, property.Name)))
            {
                command.Parameters.Add(CreateParameter(property, parameters));
            }

            return command;
        }

        public async Task ReadAsync(DbDataReader reader, CancellationToken cancellationToken)
        {
            Value = await read!(reader, cancellationToken);
            HasValue = true;
        }

        public void Complete(int recordsAffected)
        {
            Value = recordsAffected;
            HasValue = true;
        }
    }
}

// The result of one statement of a PostgresBatch, readable once its statement ran.
public sealed class PostgresBatchResult<T>
{
    private readonly Func<bool> hasValue;
    private readonly Func<T> read;

    internal PostgresBatchResult(Func<bool> hasValue, Func<T> read)
    {
        this.hasValue = hasValue;
        this.read = read;
    }

    // False until the batch ran, and when it failed for a statement that returns no rows and for
    // a query at or after the statement that failed.
    public bool HasValue => hasValue();

    public T Value => HasValue
        ? read()
        : throw new InvalidOperationException("The statement of this batch result has not run.");

    public PostgresBatchResult<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new PostgresBatchResult<TResult>(() => HasValue, () => selector(Value));
    }
}

public static class PostgresBatchResult
{
    // One result from the results of two statements of the same batch.
    public static PostgresBatchResult<TResult> Combine<TFirst, TSecond, TResult>(
        PostgresBatchResult<TFirst> first,
        PostgresBatchResult<TSecond> second,
        Func<TFirst, TSecond, TResult> combine)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(combine);
        return new PostgresBatchResult<TResult>(
            () => first.HasValue && second.HasValue,
            () => combine(first.Value, second.Value));
    }
}
