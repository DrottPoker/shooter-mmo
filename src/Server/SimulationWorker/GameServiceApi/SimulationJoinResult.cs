using System.Net;
using Mmorpg.GameProtocol;

namespace Mmorpg.SimulationWorker.GameServiceApi;

public sealed record SimulationJoinResult<T>(T? Value, SimulationWorkerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    // The keys of the peer's sealed datagrams on a successful join. Whoever takes the result
    // installs them for the peer, or not, and zeroes them either way.
    public RealtimeTransportKeys? TransportKeys { get; init; }

    public static SimulationJoinResult<T> Success(T value, RealtimeTransportKeys transportKeys)
    {
        ArgumentNullException.ThrowIfNull(transportKeys);
        return new SimulationJoinResult<T>(value, null, (int)HttpStatusCode.OK)
        {
            TransportKeys = transportKeys
        };
    }

    public static SimulationJoinResult<T> BadRequest(string code, string message)
    {
        return Failure((int)HttpStatusCode.BadRequest, code, message);
    }

    public static SimulationJoinResult<T> Conflict(string code, string message)
    {
        return Failure((int)HttpStatusCode.Conflict, code, message);
    }

    public static SimulationJoinResult<T> Failure(int statusCode, string code, string message)
    {
        return new SimulationJoinResult<T>(default, new SimulationWorkerErrorResponse(code, message), statusCode);
    }

}
