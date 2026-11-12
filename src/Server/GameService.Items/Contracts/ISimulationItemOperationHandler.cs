using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items.Contracts;

// Runs one operation kind of the worker item route for a module above Items, which registers it
// as a singleton. The route authenticates the worker, checks the request's core fields, builds
// the simulation actor, and hands every kind that it does not run itself to the handler of that
// kind. The handler owns the names and the checks of its own fields. A kind with two handlers,
// or a handler of a kind that the route runs itself, stops the start.
public interface ISimulationItemOperationHandler
{
    // The operation kind as the worker names it, without surrounding white space.
    string OperationKind { get; }

    Task<ServiceResult<SimulationItemOperationResponse>> ExecuteAsync(
        SimulationItemOperation operation,
        CancellationToken cancellationToken);
}
