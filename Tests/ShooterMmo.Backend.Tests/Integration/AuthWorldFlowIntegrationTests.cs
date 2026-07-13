using AuthService.Worlds;
using Microsoft.AspNetCore.Http;

namespace ShooterMmo.Backend.Tests.Integration;

public sealed class AuthWorldFlowIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task RegistrationCharacterAndWorldSessionLifecyclePersistsAcrossServices()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        await context.InitializeDatabaseAsync();

        Assert.Equal(7, await CountFoundationTablesAsync(context));
        Assert.Equal(7, await CountAppliedMigrationsAsync(context));

        var player = await context.RegisterPlayerAsync();
        var characters = await context.CharacterService.ListAsync(
            player.Registration.AccountId,
            CancellationToken.None);

        Assert.Single(characters.Value!);
        Assert.Equal(player.Character.Id, characters.Value!.Single().Id);

        var join = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-1",
            new JoinWorldRequest(player.Character.Id),
            CancellationToken.None);

        Assert.True(join.Succeeded, join.Error?.Message);
        Assert.False(join.Value!.IsReconnect);

        var wrongWorld = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(join.Value.JoinTicket, "local-world-2"),
            CancellationToken.None);

        Assert.False(wrongWorld.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, wrongWorld.StatusCode);
        Assert.Equal("wrong_world", wrongWorld.Error!.Code);
        Assert.Equal(1, await CountActiveTicketsAsync(context));

        var consumed = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(join.Value.JoinTicket, "local-world-1"),
            CancellationToken.None);

        Assert.True(consumed.Succeeded, consumed.Error?.Message);
        Assert.Equal(player.Character.Id, consumed.Value!.CharacterId);
        Assert.Equal("Integration Hero", consumed.Value.CharacterName);
        Assert.Equal("local-world-1", consumed.Value.WorldId);
        Assert.NotEqual(Guid.Empty, consumed.Value.WorldSessionId);
        Assert.False(string.IsNullOrWhiteSpace(consumed.Value.WorldSessionToken));
        Assert.False(consumed.Value.IsReconnect);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));

        var consumedAgain = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(join.Value.JoinTicket, "local-world-1"),
            CancellationToken.None);

        Assert.False(consumedAgain.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, consumedAgain.StatusCode);

        var heartbeat = await context.WorldSessionService.HeartbeatAsync(
            consumed.Value.WorldSessionId,
            new WorldSessionCredentialRequest(
                consumed.Value.WorldId,
                consumed.Value.WorldSessionToken),
            CancellationToken.None);

        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.False(heartbeat.Value!.Released);
        Assert.True(heartbeat.Value.ExpiresAt >= consumed.Value.SessionExpiresAt);

        var firstRelease = await context.WorldSessionService.ReleaseAsync(
            consumed.Value.WorldSessionId,
            new WorldSessionCredentialRequest(
                consumed.Value.WorldId,
                consumed.Value.WorldSessionToken),
            CancellationToken.None);

        var secondRelease = await context.WorldSessionService.ReleaseAsync(
            consumed.Value.WorldSessionId,
            new WorldSessionCredentialRequest(
                consumed.Value.WorldId,
                consumed.Value.WorldSessionToken),
            CancellationToken.None);

        Assert.True(firstRelease.Succeeded, firstRelease.Error?.Message);
        Assert.True(secondRelease.Succeeded, secondRelease.Error?.Message);
        Assert.True(firstRelease.Value!.Released);
        Assert.True(secondRelease.Value!.Released);
        Assert.Equal(0, await CountActiveWorldSessionsAsync(context));

        var heartbeatAfterRelease = await context.WorldSessionService.HeartbeatAsync(
            consumed.Value.WorldSessionId,
            new WorldSessionCredentialRequest(
                consumed.Value.WorldId,
                consumed.Value.WorldSessionToken),
            CancellationToken.None);

        Assert.False(heartbeatAfterRelease.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, heartbeatAfterRelease.StatusCode);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentTicketsReconnectAndCrossWorldRulesKeepOneActiveSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync(
            "concurrency@example.com",
            "concurrency_player",
            "Concurrency Hero");

        var ticketTasks = Enumerable.Range(0, 2)
            .Select(_ => context.WorldService.CreateJoinTicketAsync(
                player.Account,
                "local-world-1",
                new JoinWorldRequest(player.Character.Id),
                CancellationToken.None));

        var tickets = await Task.WhenAll(ticketTasks);

        Assert.All(tickets, result => Assert.True(result.Succeeded, result.Error?.Message));
        Assert.Equal(1, await CountActiveTicketsAsync(context));

        var consumeResults = new List<ConsumedJoinTicketResponse>();
        var rejectedTicketCount = 0;

        foreach (var ticket in tickets)
        {
            var consume = await context.WorldService.ConsumeJoinTicketAsync(
                new ConsumeJoinTicketRequest(ticket.Value!.JoinTicket, "local-world-1"),
                CancellationToken.None);

            if (consume.Succeeded)
            {
                consumeResults.Add(consume.Value!);
            }
            else
            {
                Assert.Equal(StatusCodes.Status401Unauthorized, consume.StatusCode);
                rejectedTicketCount++;
            }
        }

        var firstSession = Assert.Single(consumeResults);
        Assert.Equal(1, rejectedTicketCount);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));

        var reconnectTicket = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-1",
            new JoinWorldRequest(player.Character.Id),
            CancellationToken.None);

        Assert.True(reconnectTicket.Succeeded, reconnectTicket.Error?.Message);
        Assert.True(reconnectTicket.Value!.IsReconnect);

        var reconnect = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(reconnectTicket.Value.JoinTicket, "local-world-1"),
            CancellationToken.None);

        Assert.True(reconnect.Succeeded, reconnect.Error?.Message);
        Assert.True(reconnect.Value!.IsReconnect);
        Assert.Equal(firstSession.WorldSessionId, reconnect.Value.WorldSessionId);
        Assert.NotEqual(firstSession.WorldSessionToken, reconnect.Value.WorldSessionToken);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));

        await context.AddWorldAsync("local-world-2");

        var crossWorldWhileActive = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-2",
            new JoinWorldRequest(player.Character.Id),
            CancellationToken.None);

        Assert.False(crossWorldWhileActive.Succeeded);
        Assert.Equal(StatusCodes.Status409Conflict, crossWorldWhileActive.StatusCode);
        Assert.Equal("character_already_active", crossWorldWhileActive.Error!.Code);

        var staleTokenRelease = await context.WorldSessionService.ReleaseAsync(
            firstSession.WorldSessionId,
            new WorldSessionCredentialRequest(
                firstSession.WorldId,
                firstSession.WorldSessionToken),
            CancellationToken.None);

        Assert.False(staleTokenRelease.Succeeded);
        Assert.Equal(StatusCodes.Status401Unauthorized, staleTokenRelease.StatusCode);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));

        var release = await context.WorldSessionService.ReleaseAsync(
            reconnect.Value.WorldSessionId,
            new WorldSessionCredentialRequest(
                reconnect.Value.WorldId,
                reconnect.Value.WorldSessionToken),
            CancellationToken.None);

        Assert.True(release.Succeeded, release.Error?.Message);

        var crossWorldAfterRelease = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-2",
            new JoinWorldRequest(player.Character.Id),
            CancellationToken.None);

        Assert.True(crossWorldAfterRelease.Succeeded, crossWorldAfterRelease.Error?.Message);
        Assert.False(crossWorldAfterRelease.Value!.IsReconnect);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentMigrationInitializationAppliesEachMigrationOnce()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(initializeDatabase: false);

        await Task.WhenAll(
            context.InitializeDatabaseAsync(),
            context.InitializeDatabaseAsync());

        Assert.Equal(7, await CountFoundationTablesAsync(context));
        Assert.Equal(7, await CountAppliedMigrationsAsync(context));
        Assert.Equal(
            3,
            await context.ExecuteScalarIntAsync(
                """
                select count(*)
                from pg_indexes
                where schemaname = 'public'
                  and indexname in (
                      'ux_account_sessions_active_account',
                      'ux_character_world_sessions_active_character',
                      'ux_world_join_tickets_active_character');
                """));
    }

    [PostgresIntegrationFact]
    public async Task SecondLoginReplacesThePreviousSessionAndAllOfItsWorldAccess()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var firstJoin = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-1",
            new JoinWorldRequest(player.Character.Id),
            CancellationToken.None);
        Assert.True(firstJoin.Succeeded, firstJoin.Error?.Message);

        var firstWorldSession = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(firstJoin.Value!.JoinTicket, "local-world-1"),
            CancellationToken.None);
        Assert.True(firstWorldSession.Succeeded, firstWorldSession.Error?.Message);

        var secondCharacter = await context.CharacterService.CreateAsync(
            player.Registration.AccountId,
            new AuthService.Characters.CreateCharacterRequest("Second Hero"),
            CancellationToken.None);
        Assert.True(secondCharacter.Succeeded, secondCharacter.Error?.Message);

        var pendingSecondCharacterTicket = await context.WorldService.CreateJoinTicketAsync(
            player.Account,
            "local-world-1",
            new JoinWorldRequest(secondCharacter.Value!.Id),
            CancellationToken.None);
        Assert.True(
            pendingSecondCharacterTicket.Succeeded,
            pendingSecondCharacterTicket.Error?.Message);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));
        Assert.Equal(1, await CountActiveTicketsAsync(context));

        var secondLogin = await context.AccountService.LoginAsync(
            new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
            CancellationToken.None);

        Assert.True(secondLogin.Succeeded, secondLogin.Error?.Message);

        var replacedSession = await context.SessionService.ResolveTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None);
        Assert.Null(replacedSession.Account);
        Assert.Equal(
            AuthService.Auth.AccountSessionErrorCode.SessionReplaced,
            replacedSession.ErrorCode);

        var secondAccount = await context.SessionService.AuthenticateTokenAsync(
            secondLogin.Value!.SessionToken,
            CancellationToken.None);
        Assert.NotNull(secondAccount);
        Assert.Equal(1, await CountActiveAccountSessionsAsync(context));
        Assert.Equal(0, await CountActiveTicketsAsync(context));
        Assert.Equal(0, await CountActiveWorldSessionsAsync(context));

        var replacedWorldHeartbeat = await context.WorldSessionService.HeartbeatAsync(
            firstWorldSession.Value!.WorldSessionId,
            new WorldSessionCredentialRequest(
                firstWorldSession.Value.WorldId,
                firstWorldSession.Value.WorldSessionToken),
            CancellationToken.None);
        Assert.False(replacedWorldHeartbeat.Succeeded);
        Assert.Equal(
            AuthService.Auth.AccountSessionErrorCode.SessionReplaced,
            replacedWorldHeartbeat.Error!.Code);

        var replacementJoin = await context.WorldService.CreateJoinTicketAsync(
            secondAccount!,
            "local-world-1",
            new JoinWorldRequest(secondCharacter.Value.Id),
            CancellationToken.None);
        Assert.True(replacementJoin.Succeeded, replacementJoin.Error?.Message);

        var replacementWorldSession = await context.WorldService.ConsumeJoinTicketAsync(
            new ConsumeJoinTicketRequest(replacementJoin.Value!.JoinTicket, "local-world-1"),
            CancellationToken.None);
        Assert.True(replacementWorldSession.Succeeded, replacementWorldSession.Error?.Message);
        Assert.Equal(secondCharacter.Value.Id, replacementWorldSession.Value!.CharacterId);
        Assert.Equal(1, await CountActiveWorldSessionsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentLoginsLeaveExactlyOneActiveAccountSession()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync();
        var player = await context.RegisterPlayerAsync();

        var logins = await Task.WhenAll(
            context.AccountService.LoginAsync(
                new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
                CancellationToken.None),
            context.AccountService.LoginAsync(
                new AuthService.Auth.LoginAccountRequest("integration_player", "TestPass123!"),
                CancellationToken.None));

        Assert.All(logins, login => Assert.True(login.Succeeded, login.Error?.Message));
        Assert.Null(await context.SessionService.AuthenticateTokenAsync(
            player.Registration.SessionToken,
            CancellationToken.None));
        var authenticatedLoginCount = 0;
        foreach (var login in logins)
        {
            if (await context.SessionService.AuthenticateTokenAsync(
                    login.Value!.SessionToken,
                    CancellationToken.None) is not null)
            {
                authenticatedLoginCount++;
            }
        }

        Assert.Equal(1, authenticatedLoginCount);
        Assert.Equal(1, await CountActiveAccountSessionsAsync(context));
    }

    [PostgresIntegrationFact]
    public async Task SeedWorldRequiresFreshHeartbeatToBeOnline()
    {
        await using var context = await PostgresIntegrationTestContext.CreateAsync(
            heartbeatSeedWorld: false);

        var beforeHeartbeat = await context.WorldService.ListWorldsAsync(CancellationToken.None);
        var offlineWorld = Assert.Single(beforeHeartbeat.Value!);
        Assert.False(offlineWorld.IsOnline);
        Assert.Null(offlineWorld.LastHeartbeatAt);

        var heartbeat = await context.WorldRegistryService.HeartbeatAsync(
            "local-world-1",
            PostgresIntegrationTestContext.CreateHeartbeatRequest(
                host: "world.test.local",
                udpPort: 28015),
            CancellationToken.None);
        Assert.True(heartbeat.Succeeded, heartbeat.Error?.Message);
        Assert.Equal("world.test.local", heartbeat.Value!.Host);
        Assert.Equal(28015, heartbeat.Value.UdpPort);
        Assert.Equal(4, heartbeat.Value.ProtocolVersion);

        var afterHeartbeat = await context.WorldService.ListWorldsAsync(CancellationToken.None);
        var onlineWorld = Assert.Single(afterHeartbeat.Value!);
        Assert.True(onlineWorld.IsOnline);
        Assert.Equal("world.test.local", onlineWorld.Host);
        Assert.Equal(28015, onlineWorld.UdpPort);
        Assert.NotNull(onlineWorld.LastHeartbeatAt);
        Assert.True(onlineWorld.OnlineUntil > onlineWorld.LastHeartbeatAt);

        await context.ExecuteAsync(
            "update worlds set last_heartbeat_at = now() - interval '1 hour';");

        var afterTimeout = await context.WorldService.ListWorldsAsync(CancellationToken.None);
        Assert.False(Assert.Single(afterTimeout.Value!).IsOnline);

        var replacementHeartbeat = await context.WorldRegistryService.HeartbeatAsync(
            "local-world-1",
            PostgresIntegrationTestContext.CreateHeartbeatRequest("replacement-instance"),
            CancellationToken.None);
        Assert.True(replacementHeartbeat.Succeeded, replacementHeartbeat.Error?.Message);

        var staleOffline = await context.WorldRegistryService.MarkOfflineAsync(
            "local-world-1",
            new WorldOfflineRequest("integration-world-instance"),
            CancellationToken.None);
        Assert.False(staleOffline.Succeeded);
        Assert.Equal("world_instance_changed", staleOffline.Error!.Code);

        var offline = await context.WorldRegistryService.MarkOfflineAsync(
            "local-world-1",
            new WorldOfflineRequest("replacement-instance"),
            CancellationToken.None);
        Assert.True(offline.Succeeded, offline.Error?.Message);

        var afterOffline = await context.WorldService.ListWorldsAsync(CancellationToken.None);
        Assert.False(Assert.Single(afterOffline.Value!).IsOnline);
    }

    private static Task<int> CountFoundationTablesAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            """
            select count(*)
            from information_schema.tables
            where table_schema = 'public'
              and table_name in (
                  'accounts',
                  'account_sessions',
                  'characters',
                  'worlds',
                  'world_join_tickets',
                  'character_world_sessions',
                  'schema_migrations');
            """);
    }

    private static Task<int> CountAppliedMigrationsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync("select count(*) from schema_migrations;");
    }

    private static Task<int> CountActiveTicketsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from world_join_tickets where consumed_at is null;");
    }

    private static Task<int> CountActiveAccountSessionsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from account_sessions where revoked_at is null;");
    }

    private static Task<int> CountActiveWorldSessionsAsync(PostgresIntegrationTestContext context)
    {
        return context.ExecuteScalarIntAsync(
            "select count(*) from character_world_sessions where released_at is null;");
    }
}
