using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mmorpg.GameService.Platform.Http;
using Npgsql;

namespace Mmorpg.GameService.Platform.Tests;

public sealed class ApiPipelineTests
{
    [Fact]
    public async Task NpgsqlConnectionFailureReturnsStableServiceUnavailableProblem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();

        await using var application = builder.Build();
        application.UseApiPipeline();
        application.MapGet(
            "/database-failure",
            (Func<IResult>)(() =>
                throw new PostgresException(
                    "sorry, too many clients already",
                    "FATAL",
                    "FATAL",
                    PostgresErrorCodes.TooManyConnections)));
        await application.StartAsync(cancellationToken);

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses
            ?? throw new InvalidOperationException("The test server has no address feature.");
        using var client = new HttpClient
        {
            BaseAddress = new Uri(addresses.Single())
        };

        using var response = await client.GetAsync("/database-failure", cancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<DatabaseProblem>(
            cancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(problem);
        Assert.Equal("database_unavailable", problem.Code);

        await application.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task ExceptionResponsesKeepTheCorrelationIdHeader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();

        await using var application = builder.Build();
        application.UseApiPipeline();
        application.MapGet(
            "/unexpected-failure",
            (Func<IResult>)(() => throw new InvalidOperationException("boom")));
        await application.StartAsync(cancellationToken);
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/unexpected-failure");
        request.Headers.Add(ApiPipelineExtensions.CorrelationIdHeaderName, "trace-123");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(
            ApiPipelineExtensions.CorrelationIdHeaderName,
            out var correlationIds));
        Assert.Equal("trace-123", Assert.Single(correlationIds));

        await application.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task ARequestWithoutACorrelationIdUsesItsTraceId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        await using var application = await StartAsync(builder => { }, cancellationToken);
        using var client = CreateClient(application);
        var traceId = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/log");
        request.Headers.Add("traceparent", $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            traceId.ToHexString(),
            Assert.Single(response.Headers.GetValues(ApiPipelineExtensions.CorrelationIdHeaderName)));
        await application.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task EveryLogEntryOfARequestCarriesItsCorrelationId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logs = new ScopeCapturingLoggerProvider();
        await using var application = await StartAsync(
            builder => builder.Logging.AddProvider(logs),
            cancellationToken);
        using var client = CreateClient(application);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/log");
        request.Headers.Add(ApiPipelineExtensions.CorrelationIdHeaderName, "playtest-42");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(logs.Entries, entry => entry.Message == "Inside the request.");
        Assert.Contains(
            new KeyValuePair<string, object?>("CorrelationId", "playtest-42"),
            entry.ScopeValues);
        await application.StopAsync(cancellationToken);
    }

    private static async Task<WebApplication> StartAsync(
        Action<WebApplicationBuilder> configure,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddApiProblemDetails();
        configure(builder);

        var application = builder.Build();
        application.UseApiPipeline();
        application.MapGet("/log", (ILogger<ApiPipelineTests> logger) =>
        {
            logger.LogInformation("Inside the request.");
            return Results.Ok();
        });
        await application.StartAsync(cancellationToken);
        return application;
    }

    private static HttpClient CreateClient(WebApplication application)
    {
        var address = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private sealed record DatabaseProblem(string Code);

    private sealed record CapturedLogEntry(
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> ScopeValues);

    private sealed class ScopeCapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ScopeCapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class ScopeCapturingLogger(ScopeCapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider.scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = new List<KeyValuePair<string, object?>>();
                provider.scopes.ForEachScope(
                    (scope, collected) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        {
                            collected.AddRange(pairs);
                        }
                    },
                    values);
                provider.Entries.Enqueue(new CapturedLogEntry(formatter(state, exception), values));
            }
        }
    }
}
