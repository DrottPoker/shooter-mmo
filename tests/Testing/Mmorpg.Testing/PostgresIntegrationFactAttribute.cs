using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Mmorpg.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresIntegrationFactAttribute : FactAttribute, ITraitAttribute
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

    // Lets filters select or exclude every infrastructure test without a class-level trait.
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        return
        [
            new(TestCategories.Category, TestCategories.Integration),
            new(TestCategories.Infrastructure, TestCategories.Postgres)
        ];
    }
}
