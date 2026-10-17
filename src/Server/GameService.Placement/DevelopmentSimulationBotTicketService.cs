using Mmorpg.GameService.Platform.Http;
using Mmorpg.Server.Common.Identity;

namespace Mmorpg.GameService.Placement;

internal sealed class DevelopmentSimulationBotTicketService(
    DevelopmentSimulationBotPlacementService placementService,
    DevelopmentSimulationBotAuthority authority)
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
        return placement is null
            ? ServiceResult<DevelopmentSimulationBotTicketResponse>.Conflict(
                "development_bot_placement_unavailable",
                "The requested shard has no healthy assigned simulation worker.")
            : authority.IssueTicket(request, placement);
    }
}
