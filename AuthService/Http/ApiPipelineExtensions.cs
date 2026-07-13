using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.Http;

public static class ApiPipelineExtensions
{
    public const string CorrelationIdHeaderName = "X-Correlation-ID";

    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions.TryAdd(
                    "correlationId",
                    context.HttpContext.TraceIdentifier);
            };
        });

        return services;
    }

    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var httpContext = statusCodeContext.HttpContext;
            await Results.Problem(
                    statusCode: httpContext.Response.StatusCode,
                    title: ReasonPhrases.GetReasonPhrase(httpContext.Response.StatusCode),
                    detail: "The request could not be completed.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "http_error"
                    })
                .ExecuteAsync(httpContext);
        });
        app.UseMiddleware<SensitiveResponseCacheMiddleware>();

        return app;
    }
}

internal sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var isBadRequest = exception is BadHttpRequestException;
        var statusCode = isBadRequest
            ? StatusCodes.Status400BadRequest
            : StatusCodes.Status500InternalServerError;

        if (isBadRequest)
        {
            logger.LogWarning(
                "Rejected malformed request {Method} {Path} with correlation id {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogError(
                exception,
                "Unhandled API failure for {Method} {Path} with correlation id {CorrelationId}.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = statusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = statusCode,
                Title = ReasonPhrases.GetReasonPhrase(statusCode),
                Detail = isBadRequest
                    ? "The request body or parameters are invalid."
                    : "An unexpected server error occurred.",
                Extensions =
                {
                    ["code"] = isBadRequest ? "invalid_request" : "internal_server_error"
                }
            }
        });
    }
}

internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var suppliedId = context.Request.Headers[ApiPipelineExtensions.CorrelationIdHeaderName].ToString();
        var correlationId = IsValid(suppliedId)
            ? suppliedId
            : Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.Headers[ApiPipelineExtensions.CorrelationIdHeaderName] = correlationId;

        await next(context);
    }

    private static bool IsValid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');
    }
}

internal sealed class SensitiveResponseCacheMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SensitiveResponseAttribute>() is not null)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                return Task.CompletedTask;
            });
        }

        await next(context);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SensitiveResponseAttribute : Attribute;
