using System.Runtime.CompilerServices;

namespace ShooterMmo.Backend.Tests.Integration;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "SHOOTER_MMO_TEST_POSTGRES";

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
