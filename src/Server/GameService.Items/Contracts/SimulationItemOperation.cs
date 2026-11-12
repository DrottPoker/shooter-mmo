using System.Text.Json;
using Mmorpg.GameService.Platform.Http;

namespace Mmorpg.GameService.Items.Contracts;

// One operation of the worker item route as the route established it, for the handler of its
// kind: the authenticated worker and simulation session, the request's core fields, the fields
// that only the handler knows, and the simulation actor with the live access that the worker
// validated. The handler reads its fields with ReadFields and ReadAccessFields and answers with
// RunAsync or Unsupported.
public sealed class SimulationItemOperation
{
    private readonly SimulationItemMutationService route;
    private readonly SimulationItemOperationRequest request;
    private readonly JsonSerializerOptions jsonOptions;

    internal SimulationItemOperation(
        SimulationItemMutationService route,
        Guid simulationSessionId,
        string workerId,
        string operationKind,
        SimulationItemOperationRequest request,
        ItemTransactionActor actor,
        JsonSerializerOptions jsonOptions)
    {
        this.route = route;
        this.request = request;
        this.jsonOptions = jsonOptions;
        SimulationSessionId = simulationSessionId;
        WorkerId = workerId;
        OperationKind = operationKind;
        Actor = actor;
    }

    public Guid SimulationSessionId { get; }

    // The authenticated worker, which the request named.
    public string WorkerId { get; }

    public string OperationKind { get; }

    // The simulation worker actor with the city service access that the worker validated. A
    // handler adds the capability claims of its kind before it runs a command.
    public ItemTransactionActor Actor { get; }

    public Guid OperationId => request.OperationId;

    public Guid AccountId => request.AccountId;

    public Guid CharacterId => request.CharacterId;

    public long ExpectedCharacterRevision => request.ExpectedCharacterRevision;

    public Guid? ItemInstanceId => request.ItemInstanceId;

    public long? ExpectedItemRevision => request.ExpectedItemRevision;

    public Guid? TargetItemInstanceId => request.TargetItemInstanceId;

    public long? ExpectedTargetItemRevision => request.ExpectedTargetItemRevision;

    public Guid? DestinationContainerId => request.DestinationContainerId;

    public int? DestinationSlotIndex => request.DestinationSlotIndex;

    public int? Quantity => request.Quantity;

    public string? EquipmentSlotId => request.EquipmentSlotId;

    public Guid? InteractionSessionId => request.InteractionSessionId;

    public string? CapabilityId => request.CapabilityId;

    // The request names an item and its expected revision, as the route requires of an item it
    // acts on.
    public bool HasItem => SimulationItemMutationService.HasItem(request);

    // The request names the interaction and the capability that the worker validated. The
    // capability id is content, so only its presence counts.
    public bool HasInteraction => SimulationItemMutationService.HasNpcContext(request);

    // The request fields that the route does not know, read with the route's JSON options. A
    // field that does not read as its type refuses the request as the route refuses a body it
    // cannot read, and the handler does not continue.
    public TFields ReadFields<TFields>()
        where TFields : class
    {
        return SimulationItemOperationBinding.Read<TFields>(request.Fields, jsonOptions);
    }

    // The same for the fields of the request's access.
    public TFields ReadAccessFields<TFields>()
        where TFields : class
    {
        return SimulationItemOperationBinding.Read<TFields>(request.Access!.Fields, jsonOptions);
    }

    // Runs the command under this operation's id and answers the worker: a committed transaction
    // with the item response and the character's state afterwards, a rejected one as
    // mapRejection maps its error. A module maps its own codes there and falls back to
    // SimulationItemRejections for Items' codes.
    public Task<ServiceResult<SimulationItemOperationResponse>> RunAsync<TCommand>(
        ItemTransactionActor actor,
        TCommand command,
        Func<ItemTransactionResult, ServiceResult<SimulationItemOperationResponse>> mapRejection,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(mapRejection);
        return route.RunAsync(
            new ItemTransactionRequest<TCommand>(OperationId, actor, command),
            CharacterId,
            mapRejection,
            cancellationToken);
    }

    // The route's answer to a request that its kind cannot run, such as a missing or invalid
    // field.
    public ServiceResult<SimulationItemOperationResponse> Unsupported()
    {
        return SimulationItemMutationService.Unsupported();
    }
}
