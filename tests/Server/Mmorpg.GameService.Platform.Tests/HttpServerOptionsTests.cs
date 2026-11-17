using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Mmorpg.GameService.Platform.Configuration;
using Mmorpg.Testing;

namespace Mmorpg.GameService.Platform.Tests;

public sealed partial class HttpServerOptionsTests
{
    [Fact]
    public void TheDefaultsTrustNoProxyAndKeepTheDocumentedLimits()
    {
        var options = Read([]);

        Assert.False(options.TrustsProxies);
        Assert.Equal(1, options.ForwardLimit);
        Assert.Equal(16 * 1024, options.MaxRequestHeadersTotalSizeBytes);
        Assert.Equal(64, options.MaxRequestHeaderCount);
        Assert.Equal(4 * 1024, options.MaxRequestLineSizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(15), options.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(130), options.KeepAliveTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ShutdownDrain);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ShutdownTimeout);
    }

    [Fact]
    public void TrustedProxiesAreAddressesAndNetworks()
    {
        var options = Read(new() { [HttpServerOptions.TrustedProxiesKey] = "10.0.0.5; 172.16.0.0/12, ::1" });

        Assert.True(options.TrustsProxies);
        Assert.Equal([IPAddress.Parse("10.0.0.5"), IPAddress.IPv6Loopback], options.TrustedProxies);
        Assert.Equal([IPNetwork.Parse("172.16.0.0/12")], options.TrustedNetworks);
    }

    [Theory]
    [InlineData(HttpServerOptions.TrustedProxiesKey, "proxy.example", "must be an IP address or a network")]
    [InlineData(HttpServerOptions.TrustedProxiesKey, "10.0.0.1/8", "must be an IP address or a network")]
    [InlineData(HttpServerOptions.ForwardLimitKey, "0", "from 1 to 10")]
    [InlineData(HttpServerOptions.MaxRequestHeaderCountKey, "5", "from 16 to 1000")]
    [InlineData(HttpServerOptions.ShutdownDrainKey, "30", "must be longer than Http:ShutdownDrainSeconds")]
    [InlineData(HttpServerOptions.ShutdownTimeoutKey, "10", "must be longer than Http:ShutdownDrainSeconds")]
    public void InvalidSettingsAreRefused(string key, string value, string expected)
    {
        var errors = new List<string>();

        HttpServerOptions.Read(Configuration(new() { [key] = value }), errors);

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("api.example.com;*")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("api.example.com;0.0.0.0")]
    [InlineData("api.example.com; [::] ")]
    public void OutsideDevelopmentAllowedHostsMustNameRealHosts(string? allowedHosts)
    {
        var options = Read(new() { [HttpServerOptions.AllowedHostsKey] = allowedHosts });

        var error = Assert.Single(options.ErrorsOutsideDevelopment());
        Assert.Contains("AllowedHosts must name the host names", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RealHostNamesPassOutsideDevelopment()
    {
        var options = Read(new() { [HttpServerOptions.AllowedHostsKey] = "api.example.com;*.example.net;game-service" });

        Assert.Empty(options.ErrorsOutsideDevelopment());
    }

    [Theory]
    [InlineData("Password=replace-with-local-postgres-password", "127.0.0.1:6379,password=a-real-redis-secret", "ConnectionStrings:Postgres")]
    [InlineData("Password=stack_stress_local_only", "127.0.0.1:6379,password=a-real-redis-secret", "ConnectionStrings:Postgres")]
    [InlineData("Password=a-real-postgres-secret", "127.0.0.1:6379", "ConnectionStrings:Redis")]
    [InlineData("Password=a-real-postgres-secret", "127.0.0.1:6379,password=replace-with-redis-password", "ConnectionStrings:Redis")]
    public void OutsideDevelopmentConnectionPasswordsMustBeReal(string postgresPassword, string redis, string expected)
    {
        var options = Platform(postgresPassword, redis);

        var error = Assert.Single(options.ErrorsOutsideDevelopment());
        Assert.StartsWith(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void RealPasswordsAndHostsPassOutsideDevelopment()
    {
        var options = Platform("Password=a-real-postgres-secret", "redis:6379,password=a-real-redis-secret");

        Assert.Empty(options.ErrorsOutsideDevelopment());
    }

    // Every literal password in the repository's Compose files and CI workflow is public, so
    // GameService refuses each one outside Development.
    [Fact]
    public void EveryPasswordThatTheRepositoryPublishesIsAPlaceholder()
    {
        var files = Directory.GetFiles(RepositoryPaths.Root, "docker-compose*.yml")
            .Concat(Directory.GetFiles(Path.Combine(RepositoryPaths.Root, ".github", "workflows"), "*.yml"))
            .ToArray();
        var passwords = files
            .SelectMany(file => PasswordPattern().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["password"].Value.Trim().Trim('"', '\''))
            .Where(password => !password.StartsWith("${", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);
        Assert.NotEmpty(passwords);
        Assert.All(passwords, password => Assert.True(
            ConfigurationValidation.IsPlaceholderPassword(password),
            $"The repository publishes the password '{password}'; add it to ConfigurationValidation.RepositoryPasswords."));
        Assert.All(ConfigurationValidation.RepositoryPasswords, password => Assert.Contains(password, passwords));
    }

    private static HttpServerOptions Read(Dictionary<string, string?> settings)
    {
        var errors = new List<string>();
        var options = HttpServerOptions.Read(Configuration(settings), errors);
        Assert.Empty(errors);
        return options;
    }

    private static PlatformOptions Platform(string postgresPassword, string redis)
    {
        return new PlatformOptions(
            "Host=database.example;Database=game;Username=game;" + postgresPassword,
            redis,
            TimeSpan.FromSeconds(1),
            false)
        {
            Http = HttpServerOptions.Default with { AllowedHosts = "api.example.com" }
        };
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    // A Compose or workflow PostgreSQL password, a password in a connection string, or a Redis
    // requirepass.
    [GeneratedRegex(@"(?:POSTGRES_PASSWORD:\s*|;Password=|requirepass\s+)(?<password>[^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPattern();
}
