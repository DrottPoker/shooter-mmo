using System.Net;
using System.Net.Http.Json;
using AuthService.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ShooterMmo.Backend.Tests.Unit;

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

    private sealed record DatabaseProblem(string Code);
}
