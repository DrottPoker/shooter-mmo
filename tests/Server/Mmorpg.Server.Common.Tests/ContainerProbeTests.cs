using System.Net;
using System.Net.Sockets;
using Mmorpg.Server.Common.Health;

namespace Mmorpg.Server.Common.Tests;

// The container health check that a service answers itself, because its image has no shell, curl,
// or wget: `probe http <url>` and `probe file <path> <seconds>`, exit code 0 healthy, 1 not.
public sealed class ContainerProbeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mmorpg-probe-" + Guid.NewGuid().ToString("N"));

    public ContainerProbeTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
    }

    [Theory]
    [InlineData("probe", true)]
    [InlineData("PROBE", true)]
    [InlineData("migrate", false)]
    [InlineData("--probe", false)]
    public void OnlyTheFirstArgumentSelectsTheProbe(string first, bool expected)
    {
        Assert.Equal(expected, ContainerProbe.IsProbe([first, "http", "http://localhost/"]));
        Assert.False(ContainerProbe.IsProbe([]));
        Assert.False(ContainerProbe.IsProbe(["http", first]));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0, "Healthy")]
    [InlineData(HttpStatusCode.NoContent, 0, "Healthy")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, "Unhealthy")]
    [InlineData(HttpStatusCode.NotFound, 1, "Unhealthy")]
    [InlineData(HttpStatusCode.TooManyRequests, 1, "Unhealthy")]
    public async Task AnHttpProbeIsHealthyOnA2xxAnswer(HttpStatusCode status, int expectedExitCode, string expectedLine)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));

        var (exitCode, output) = await RunAsync(["probe", "http", "http://localhost:8080/health/ready"], handler);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.StartsWith(expectedLine, output, StringComparison.Ordinal);
        Assert.Contains($"localhost:8080/health/ready answered {(int)status}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpProbeAsksForTheUrlItWasGiven()
    {
        Uri? requested = null;
        using var handler = new StubHandler((request, _) =>
        {
            requested = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await RunAsync(["probe", "http", "http://localhost:8080/health/ready?x=1"], handler);

        Assert.Equal(new Uri("http://localhost:8080/health/ready?x=1"), requested);
    }

    [Fact]
    public async Task AnHttpProbeThatDoesNotAnswerInTimeIsUnhealthy()
    {
        using var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (exitCode, output) = await RunAsync(["probe", "http", "http://localhost:8080/health/ready", "1"], handler);

        Assert.Equal(1, exitCode);
        Assert.Contains("did not answer within 1 s", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpProbeDoesNotPrintTheQueryOfItsUrl()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("refused"));

        var (exitCode, output) = await RunAsync(["probe", "http", "http://localhost:8080/health/ready?token=hunter2"], handler);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("hunter2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpProbeOfAClosedPortIsUnhealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var (exitCode, output) = await RunAsync(["probe", "http", $"http://127.0.0.1:{port}/health/ready", "2"], null);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Unhealthy", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpProbeReachesARealServer()
    {
        using var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{FreePort()}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = index == 0 ? 200 : 503;
                context.Response.Close();
            }
        }, TestContext.Current.CancellationToken);

        var (healthy, _) = await RunAsync(["probe", "http", prefix + "health/ready"], null);
        var (unhealthy, _) = await RunAsync(["probe", "http", prefix + "health/ready"], null);
        await serving;

        Assert.Equal(0, healthy);
        Assert.Equal(1, unhealthy);
    }

    [Fact]
    public async Task AFileProbeIsHealthyWhileTheFileIsFreshAndNotAfter()
    {
        var path = Path.Combine(directory, "worker-ready");
        File.WriteAllText(path, "ready");
        var now = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        var fresh = await RunAsync(["probe", "file", path, "20"], null, new FixedTimeProvider(now.AddSeconds(5)));
        var edge = await RunAsync(["probe", "file", path, "20"], null, new FixedTimeProvider(now.AddSeconds(20)));
        var stale = await RunAsync(["probe", "file", path, "20"], null, new FixedTimeProvider(now.AddSeconds(21)));

        Assert.Equal(0, fresh.ExitCode);
        Assert.Contains("was written 5 s ago", fresh.Output, StringComparison.Ordinal);
        Assert.Equal(0, edge.ExitCode);
        Assert.Equal(1, stale.ExitCode);
        Assert.Contains("more than 20 s", stale.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFileIsUnhealthy()
    {
        var (exitCode, output) = await RunAsync(["probe", "file", Path.Combine(directory, "none"), "20"], null);

        Assert.Equal(1, exitCode);
        Assert.Contains("does not exist", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("probe", "http")]
    [InlineData("probe", "ftp", "ftp://host/")]
    [InlineData("probe", "http", "not-a-url")]
    [InlineData("probe", "http", "file:///etc/passwd")]
    [InlineData("probe", "http", "http://localhost/", "0")]
    [InlineData("probe", "http", "http://localhost/", "61")]
    [InlineData("probe", "http", "http://localhost/", "soon")]
    [InlineData("probe", "file", "/tmp/x")]
    [InlineData("probe", "file", "/tmp/x", "0")]
    [InlineData("probe", "file", "/tmp/x", "often")]
    public async Task AWrongCommandLineIsUnhealthyAndSaysWhy(params string[] args)
    {
        var (exitCode, output) = await RunAsync(args, null);

        Assert.Equal(1, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string[] args,
        HttpMessageHandler? handler,
        TimeProvider? timeProvider = null)
    {
        var output = new StringWriter();
        var exitCode = await ContainerProbe.RunAsync(
            args,
            output,
            TestContext.Current.CancellationToken,
            handler,
            timeProvider);
        return (exitCode, output.ToString());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return respond(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
