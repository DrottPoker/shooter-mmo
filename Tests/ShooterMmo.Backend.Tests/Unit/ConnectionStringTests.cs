using ShooterMmo.Shared.Networking;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class ConnectionStringTests
{
    [Fact]
    public void PostgresParserReadsHostAliasAndPort()
    {
        var endpoint = PostgresConnectionString.ParseEndpoint("Server=db.internal;Port=5544;Database=game");

        Assert.Equal("db.internal", endpoint.Host);
        Assert.Equal(5544, endpoint.Port);
    }

    [Fact]
    public void PostgresParserUsesDefaultsForMissingConfiguration()
    {
        var endpoint = PostgresConnectionString.ParseEndpoint(null);

        Assert.Equal("localhost", endpoint.Host);
        Assert.Equal(5432, endpoint.Port);
    }

    [Fact]
    public void RedisParserReadsTheFirstEndpoint()
    {
        var endpoint = RedisConnectionString.ParseEndpoint("cache.internal:6380,ssl=true");

        Assert.Equal("cache.internal", endpoint.Host);
        Assert.Equal(6380, endpoint.Port);
    }
}
