using System.Runtime.CompilerServices;

namespace Mmorpg.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = PostgresTestDatabase.ConnectionStringVariable;

    public PostgresIntegrationFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = $"Set {ConnectionStringVariable} to a dedicated PostgreSQL test database.";
        }
    }
}
