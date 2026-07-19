using System.Net;
using System.Net.Http.Json;
using ShooterMmo.Tools.StackStressGenerator;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class FullStackStressClientTests
{
    [Fact]
    public async Task RegisterUsesNormalAccountEndpointAndRecordsLatency()
    {
        string? requestPath = null;
        string? requestBody = null;
        using var handler = new StubHttpMessageHandler(async request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new FullStackAuthResponse(
                    Guid.NewGuid(),
                    "stress_run_1",
                    Guid.NewGuid(),
                    "session-token",
                    DateTime.UtcNow.AddHours(1)))
            };
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5000")
        };
        var metrics = new FullStackStressMetrics(1337);
        var client = new FullStackStressClient(httpClient, metrics);

        var response = await client.RegisterAsync(
            "stress@example.test",
            "stress_run_1",
            "TestPassword123!",
            CancellationToken.None);

        Assert.Equal("/api/accounts/register", requestPath);
        Assert.Contains("stress_run_1", requestBody, StringComparison.Ordinal);
        Assert.Equal("session-token", response.SessionToken);
        var operation = metrics.Capture()["account_register"];
        Assert.Equal(1, operation.Requests);
        Assert.Equal(1, operation.Successes);
        Assert.Equal(0, operation.Failures);
        Assert.NotNull(operation.Latency);
    }

    [Fact]
    public async Task RateLimitProblemPreservesStableFailureCode()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new
                {
                    code = "rate_limit_exceeded",
                    detail = "Too many authentication attempts."
                })
            }));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:5000")
        };
        var metrics = new FullStackStressMetrics(1337);
        var client = new FullStackStressClient(httpClient, metrics);

        var exception = await Assert.ThrowsAsync<FullStackApiException>(() =>
            client.LoginAsync(
                "stress_run_1",
                "TestPassword123!",
                CancellationToken.None));

        Assert.Equal("rate_limit_exceeded", exception.Code);
        Assert.Equal(429, exception.StatusCode);
        var operation = metrics.Capture()["account_login"];
        Assert.Equal(1, operation.Failures);
        Assert.Equal(1, operation.FailureCodes["rate_limit_exceeded"]);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return handler(request);
        }
    }
}
