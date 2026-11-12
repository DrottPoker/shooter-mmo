using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mmorpg.GameService.Items;

// Reads the fields of a worker item operation that only the handler of its kind knows, and
// refuses a field that does not read as its type exactly as the route's JSON binding refuses
// the request body: with the same exception in Development, which the API pipeline answers,
// and otherwise with the bare 400 status, which the status code pages complete, after the
// binding's Debug entry.
internal static class SimulationItemOperationBinding
{
    // The route's body parameter, as the binding names it.
    private const string ParameterType = "SimulationItemOperationRequest";
    private const string ParameterName = "request";

    // The binding's logger category and entry.
    private const string BindingLoggerCategory = "Microsoft.AspNetCore.Http.RequestDelegateFactory";

    private static readonly Action<ILogger, string, string, Exception?> InvalidJsonRequestBody =
        LoggerMessage.Define<string, string>(
            LogLevel.Debug,
            new EventId(2, "InvalidJsonRequestBody"),
            "Failed to read parameter \"{ParameterType} {ParameterName}\" from the request body as JSON.");

    public static TFields Read<TFields>(
        IReadOnlyDictionary<string, JsonElement>? fields,
        JsonSerializerOptions options)
        where TFields : class
    {
        try
        {
            // The names stay as sent; the route's options read them without regard to case.
            return JsonSerializer.SerializeToElement(fields ?? new Dictionary<string, JsonElement>())
                    .Deserialize<TFields>(options)
                ?? throw new InvalidOperationException(
                    $"The fields of a worker item operation read as no {typeof(TFields).Name}.");
        }
        catch (JsonException exception)
        {
            throw new SimulationItemOperationFieldsException(exception);
        }
    }

    public static IResult Refuse(HttpContext httpContext, SimulationItemOperationFieldsException refusal)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(refusal);
        var services = httpContext.RequestServices;
        if (services.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest)
        {
            throw new BadHttpRequestException(
                $"Failed to read parameter \"{ParameterType} {ParameterName}\" from the request body as JSON.",
                refusal.JsonException);
        }

        InvalidJsonRequestBody(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(BindingLoggerCategory),
            ParameterType,
            ParameterName,
            refusal.JsonException);
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }
}

// A field of a worker item operation that does not read as its type. The route answers it as its
// JSON binding answers a body it cannot read.
internal sealed class SimulationItemOperationFieldsException(JsonException exception)
    : Exception("A field of the worker item operation cannot be read as JSON.", exception)
{
    public JsonException JsonException { get; } = exception;
}
