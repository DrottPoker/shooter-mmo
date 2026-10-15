using System.Runtime.CompilerServices;
using Xunit.v3;

namespace Mmorpg.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RedisIntegrationFactAttribute : FactAttribute, ITraitAttribute
{
    public const string ConnectionStringVariable = "MMORPG_TEST_REDIS";

    public RedisIntegrationFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = $"Set {ConnectionStringVariable} to a dedicated Redis test instance.";
        }
    }

    // Lets filters select or exclude every infrastructure test without a class-level trait.
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        return
        [
            new(TestCategories.Category, TestCategories.Integration),
            new(TestCategories.Infrastructure, TestCategories.Redis)
        ];
    }
}
