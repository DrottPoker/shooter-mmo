using System.Runtime.CompilerServices;

namespace Mmorpg.Testing;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RedisIntegrationFactAttribute : FactAttribute
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
}
