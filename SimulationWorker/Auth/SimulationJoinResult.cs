using System.Net;

namespace SimulationWorker.Auth;

public sealed record SimulationJoinResult<T>(T? Value, SimulationWorkerErrorResponse? Error, int StatusCode)
{
    public bool Succeeded => Error is null;

    public static SimulationJoinResult<T> Success(T value)
    {
        return new SimulationJoinResult<T>(value, null, (int)HttpStatusCode.OK);
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
