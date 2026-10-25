using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Placement;
using Mmorpg.GameService.Platform.Security;
using Mmorpg.GameService.Testing;
using Mmorpg.Testing;
using Npgsql;

namespace Mmorpg.Integration.Tests;

// A character's look across Characters and Placement: the worker receives it with the join
// ticket and stores the changes the player makes in the game through the session.
public sealed class CharacterAppearanceFlowIntegrationTests
{
    private const string LocalWorkerId = "local-simulation-worker-1";
    private const string LocalRuntimeId = "integration-worker-runtime";
    private const string LocalShardId = "local-shard-1";

    [PostgresIntegrationFact]
    public async Task TheConsumedTicketCarriesTheLookAndTheSessionLowersAndRaisesTheHeadwear()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);

        Assert.NotNull(session.Appearance);
        Assert.Equal("Human_Male", session.Appearance.BodyModel);
        Assert.Equal(
            ["face::Stubble", "eyes::Teal", "underwear:legs:Briefs"],
            session.Appearance.Choices.Select(choice => $"{choice.Kind}:{choice.Slot}:{choice.Option}"));
        Assert.False(session.Appearance.HeadwearLowered);

        var service = context.Services.GetRequiredService<SimulationAppearanceService>();
        var lowered = await service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            Headwear(session.SimulationSessionToken, lowered: true),
            CancellationToken.None);
        Assert.True(lowered.Succeeded, lowered.Error?.Message);
        Assert.True(lowered.Value!.HeadwearLowered);
        Assert.Equal("Human_Male", lowered.Value.BodyModel);

        var raised = await service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            Headwear(session.SimulationSessionToken, lowered: false),
            CancellationToken.None);
        Assert.True(raised.Succeeded, raised.Error?.Message);
        Assert.False(raised.Value!.HeadwearLowered);
        Assert.Equal(
            0,
            await context.ExecuteScalarIntAsync(
                $"select count(*) from character_appearances where character_id = '{player.Character.Id}' and headwear_lowered;"));
    }

    [PostgresIntegrationFact]
    public async Task OnlyTheWorkerThatHoldsALiveSessionChangesItsLook()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var service = context.Services.GetRequiredService<SimulationAppearanceService>();

        var wrongToken = await service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            Headwear("another-session-token", lowered: true),
            CancellationToken.None);
        var wrongWorker = await service.ChangeAsync(
            session.SimulationSessionId,
            "another-worker",
            Headwear(session.SimulationSessionToken, lowered: true),
            CancellationToken.None);
        var unknownChange = await service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            new SimulationAppearanceChangeRequest(LocalRuntimeId, session.SimulationSessionToken, "tattoo", null, null, null),
            CancellationToken.None);
        var headwearWithoutState = await service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            new SimulationAppearanceChangeRequest(LocalRuntimeId, session.SimulationSessionToken, "headwear", null, null, null),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, wrongToken.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, wrongWorker.StatusCode);
        Assert.Equal("invalid_appearance_change", unknownChange.Error!.Code);
        Assert.Equal("invalid_appearance_change", headwearWithoutState.Error!.Code);
        Assert.Equal(0, await context.ExecuteScalarIntAsync("select count(*) from character_appearances where headwear_lowered;"));
    }

    [PostgresIntegrationFact]
    public async Task AChangeThatWaitedForAReconnectThatRotatedTheTokenIsRefused()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var service = context.Services.GetRequiredService<SimulationAppearanceService>();

        // A reconnect holds the character lock, as ShardService does, and rotates the token.
        await using var reconnect = await context.DataSource.OpenConnectionAsync();
        await using var reconnectTransaction = await reconnect.BeginTransactionAsync();
        await using (var lockCharacter = new NpgsqlCommand(
                         "select id from characters where id = @CharacterId for update;",
                         reconnect,
                         reconnectTransaction))
        {
            lockCharacter.Parameters.AddWithValue("CharacterId", player.Character.Id);
            await lockCharacter.ExecuteScalarAsync();
        }

        var change = service.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            Headwear(session.SimulationSessionToken, lowered: true),
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(change.IsCompleted);
        await using (var rotate = new NpgsqlCommand(
                         "update character_simulation_sessions set session_token_hash = @Hash where id = @SessionId;",
                         reconnect,
                         reconnectTransaction))
        {
            rotate.Parameters.AddWithValue("Hash", TokenGenerator.HashToken("reconnected-session-token"));
            rotate.Parameters.AddWithValue("SessionId", session.SimulationSessionId);
            await rotate.ExecuteNonQueryAsync();
        }

        await reconnectTransaction.CommitAsync();
        var refused = await change;

        Assert.Equal(StatusCodes.Status401Unauthorized, refused.StatusCode);
        Assert.Equal(0, await context.ExecuteScalarIntAsync("select count(*) from character_appearances where headwear_lowered;"));
    }

    [PostgresIntegrationFact]
    public async Task ADevelopmentLookChangesTheBodyOnlyInDevelopment()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();
        var session = await JoinAsync(context, player);
        var appearances = context.Services.GetRequiredService<ICharacterAppearances>();
        var request = new SimulationAppearanceChangeRequest(
            LocalRuntimeId,
            session.SimulationSessionToken,
            SimulationAppearanceService.DevelopmentLookChange,
            null,
            "Human_Female",
            [new SimulationAppearanceChoice("face", "", "Freckles"), new SimulationAppearanceChoice("hair", "", "DarkBrownHair")]);

        // The test composition, like a production GameService, has no development looks.
        var refused = await context.Services.GetRequiredService<SimulationAppearanceService>().ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            request,
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Equal("development_look_disabled", refused.Error!.Code);

        var development = new SimulationAppearanceService(
            context.DataSource,
            context.GameServiceConfiguration.Placement with { DevelopmentLooks = true },
            context.Services.GetRequiredService<ICharacterLocks>(),
            appearances);
        var changed = await development.ChangeAsync(session.SimulationSessionId, LocalWorkerId, request, CancellationToken.None);
        var unknownBody = await development.ChangeAsync(
            session.SimulationSessionId,
            LocalWorkerId,
            request with { BodyModel = "Human_Giant" },
            CancellationToken.None);

        Assert.True(changed.Succeeded, changed.Error?.Message);
        Assert.Equal("Human_Female", changed.Value!.BodyModel);
        Assert.Equal(["Freckles", "DarkBrownHair"], changed.Value.Choices.Select(choice => choice.Option));
        Assert.Equal(StatusCodes.Status400BadRequest, unknownBody.StatusCode);
        Assert.Equal("invalid_character_appearance", unknownBody.Error!.Code);
        var listed = await context.CharacterService.ListAsync(player.Registration.AccountId, CancellationToken.None);
        Assert.Equal("Human_Female", Assert.Single(listed.Value!).BodyModel);
    }

    private static async Task<ConsumedSimulationJoinTicketResponse> JoinAsync(
        PostgresIntegrationTestContext context,
        IntegrationPlayer player)
    {
        var join = await context.ShardService.CreateJoinTicketAsync(
            player.Account,
            LocalShardId,
            new JoinShardRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(join.Succeeded, join.Error?.Message);
        var consumed = await context.ShardService.ConsumeJoinTicketAsync(
            new ConsumeSimulationJoinTicketRequest(join.Value!.JoinTicket, LocalWorkerId, LocalRuntimeId, LocalShardId),
            CancellationToken.None);
        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        return consumed.Value!;
    }

    private static SimulationAppearanceChangeRequest Headwear(string sessionToken, bool lowered)
    {
        return new SimulationAppearanceChangeRequest(
            LocalRuntimeId,
            sessionToken,
            SimulationAppearanceService.HeadwearChange,
            lowered,
            null,
            null);
    }
}
