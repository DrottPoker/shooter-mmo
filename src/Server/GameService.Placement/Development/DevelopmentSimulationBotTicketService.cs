using Mmorpg.GameService.Platform.Http;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Placement.Development;

internal sealed class DevelopmentSimulationBotTicketService(
    DevelopmentSimulationBotPlacementService placementService,
    DevelopmentSimulationBotAuthority authority,
    PlacementMetrics metrics)
{
    public async Task<ServiceResult<DevelopmentSimulationBotTicketResponse>> CreateTicketAsync(
        DevelopmentSimulationBotTicketRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Identifiers.IsServiceIdentifier(request.ShardId))
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.BadRequest(
                "invalid_shard_id",
                "Shard id is invalid.");
        }

        var placement = await placementService.SelectPlacementAsync(
            request.ShardId!,
            cancellationToken);
        if (placement is null)
        {
            return ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                "development_bot_placement_unavailable",
                "The requested shard has no healthy assigned simulation worker.");
        }

        // A bot is held to the versions of its worker like a player; it has no baked World data to
        // name, so only the protocol version and the simulation revision are compared.
        var worker = new WorkerVersions(
            placement.ProtocolVersion,
            placement.SimulationRevision,
            placement.WorldId,
            placement.CollisionRevision);
        var refusal = JoinVersionCheck.Refusal(
            request.ProtocolVersion,
            request.SimulationRevision,
            worldId: null,
            collisionRevision: null,
            worker);
        if (refusal is not null)
        {
            metrics.RecordJoinRefusal(placement.ShardId, refusal);
            return JoinVersionCheck.Conflict<DevelopmentSimulationBotTicketResponse>(refusal, worker);
        }

        return authority.IssueTicket(request, placement);
    }
}
