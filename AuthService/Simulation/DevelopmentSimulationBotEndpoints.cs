using System.Net;
using System.Security.Cryptography;
using System.Text;
using AuthService.Config;
using AuthService.Http;

namespace AuthService.Simulation;

public static class DevelopmentSimulationBotEndpoints
{
    public static IEndpointRouteBuilder MapDevelopmentSimulationBotEndpoints(
        this IEndpointRouteBuilder app,
        DevelopmentSimulationBotOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return app;
        }

        app.MapPost(
                "/api/development/simulation-bots/join-tickets",
                async (
                    DevelopmentSimulationBotTicketRequest request,
                    HttpContext httpContext,
                    DevelopmentSimulationBotTicketService ticketService,
                    CancellationToken cancellationToken) =>
                {
                    if (httpContext.Connection.RemoteIpAddress is not { } remoteAddress
                        || !IPAddress.IsLoopback(remoteAddress))
                    {
                        return ServiceResult<DevelopmentSimulationBotTicketResponse>.Forbidden(
                            "development_bot_loopback_required",
                            "Development simulation bot tickets can only be issued over loopback.")
                            .ToHttpResult();
                    }

                    var suppliedSecret = httpContext.Request.Headers[
                        DevelopmentSimulationBotOptions.AuthorityHeaderName].ToString();
                    if (!FixedTimeEquals(options.AuthoritySecret, suppliedSecret))
                    {
                        return ServiceResult<DevelopmentSimulationBotTicketResponse>.Unauthorized(
                            "invalid_development_bot_authority",
                            "Development simulation bot authority credentials are invalid.")
                            .ToHttpResult();
                    }

                    var result = await ticketService.CreateTicketAsync(
                        request,
                        cancellationToken);
                    return result.ToHttpResult();
                })
            .WithMetadata(new SensitiveResponseAttribute());

        return app;
    }

    private static bool FixedTimeEquals(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
