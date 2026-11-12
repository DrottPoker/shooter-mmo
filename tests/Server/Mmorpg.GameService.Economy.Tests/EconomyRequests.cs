using System.Text.Json;
using Mmorpg.GameService.Items;

namespace Mmorpg.GameService.Economy.Tests;

// Worker item requests of the economy kinds with their economy fields, written as the worker
// sends them and kept as the route keeps the fields that it does not know.
internal static class EconomyRequests
{
    private static readonly JsonSerializerOptions WorkerJson = new(JsonSerializerDefaults.Web);

    public static SimulationItemOperationRequest WithEconomyFields(
        this SimulationItemOperationRequest request,
        EconomyOperationFields fields,
        string? vendorProfileId = null)
    {
        return request with
        {
            Access = request.Access! with { Fields = AsFields(new EconomyAccessFields(vendorProfileId)) },
            Fields = AsFields(fields)
        };
    }

    private static Dictionary<string, JsonElement> AsFields<TValue>(TValue value)
    {
        return JsonSerializer.SerializeToElement(value, WorkerJson)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
    }
}
