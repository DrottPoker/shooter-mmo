using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.GameServiceApi;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Registry;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.WorldData.Items;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A character whose connection ends while its session goes on stays in the world as a lingering
// body: others see it, it can be hit and killed, every hit keeps it its full time again, and its
// player resumes it by coming back in time. When its time is up or its death committed it goes
// with its final state and its session's release; when its session ended it goes at once.
public sealed class RealtimeLingeringBodyTests
{
    private const int TickRateHz = 30;

    [Theory]
    [InlineData("leave")]
    [InlineData("disconnect")]
    [InlineData("kick")]
    public async Task AnEndedConnectionLeavesTheBodyInTheWorldUntilItsTimeIsUp(string ending)
    {
        await using var harness = Create(bodySeconds: 2);
        var observerCharacter = harness.AddCharacter("Body Watcher");
        var character = harness.AddCharacter("Departing Hero") with
        {
            SavedState = Saved(harness, 1.5f, 2f, yaw: 30f, health: 61, mana: 17)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => observer.Spawns.Any(spawn => spawn.EntityId == entityId),
            "the observer to see the hero");

        var endedAt = DateTime.UtcNow;
        switch (ending)
        {
            case "leave":
                client.SendLeave(character.SimulationSessionId.ToString("D"));
                await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
                Assert.Equal(2, BodySecondsOf(client));
                break;
            case "disconnect":
                client.Stop();
                break;
            default:
                client.Send([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
                break;
        }

        // The body stands without a connection, other players still get it in their snapshots, and
        // its session is not released.
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        var snapshotsAtBody = observer.Snapshots.Count;
        await harness.PollUntilAsync(
            () => observer.Snapshots.Skip(snapshotsAtBody).Any(snapshot => snapshot.Entities.Any(value => value.EntityId == entityId)),
            "a snapshot of the body");
        Assert.DoesNotContain(observer.Despawns, despawn => despawn.EntityId == entityId);
        Assert.Empty(harness.GameService.Releases);

        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == entityId),
            "the body to go");
        var despawn = Despawn(observer, entityId);
        Assert.Equal(LingeringBodyTimer.TimeUpReason, despawn.Value.Reason);
        Assert.True(
            despawn.Packet.ReceivedAtUtc - endedAt >= TimeSpan.FromSeconds(1.8),
            "The body must stay its full time.");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        var release = Assert.Single(harness.GameService.Releases);
        Assert.Equal(character.SimulationSessionId, release.SimulationSessionId);
        AssertState(release.Request.FinalState, 1.5f, 2f, 30f, 61, 17);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
        Assert.DoesNotContain(harness.SessionStore.ListActiveSessions(), session => session.CharacterId == character.CharacterId);
    }

    // Hits every half second keep a body of two seconds in the world for as long as they come, and
    // it goes its full time after the last one.
    [Fact]
    public async Task EveryHitKeepsTheBodyItsFullTimeAgain()
    {
        await using var harness = Create(
            bodySeconds: 2,
            catalog: TestCatalog(minimumDamage: 1, maximumDamage: 1),
            playerVersusPlayer: true);
        var archer = harness.AddCharacter("Persistent Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Lingering Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == victimEntityId),
            "the target to appear");

        victimClient.SendLeave(victim.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(() => victimClient.Has(RealtimeMessageType.LeaveAccepted), "the leave");
        var leftAt = DateTime.UtcNow;
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));
        await harness.PollForAsync(TimeSpan.FromSeconds(4));

        Assert.True(HitsOn(archerClient, victimEntityId).Count >= 5);
        Assert.DoesNotContain(archerClient.Despawns, despawn => despawn.EntityId == victimEntityId);
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        await harness.PollUntilAsync(
            () => archerClient.Despawns.Any(despawn => despawn.EntityId == victimEntityId),
            "the body to go after the last hit");
        var despawn = Despawn(archerClient, victimEntityId);
        var lastHit = HitsOn(archerClient, victimEntityId)[^1];
        Assert.Equal(LingeringBodyTimer.TimeUpReason, despawn.Value.Reason);
        Assert.True(despawn.Packet.ReceivedAtUtc - lastHit.ReceivedAtUtc >= TimeSpan.FromSeconds(1.8));
        Assert.True(despawn.Packet.ReceivedAtUtc - leftAt >= TimeSpan.FromSeconds(5));
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        var finalState = Assert.Single(harness.GameService.Releases).Request.FinalState!;
        Assert.InRange(finalState.Health, 1, 95);
    }

    // A wolf that a body draws kills it: the death commits once, as any player's, and the body
    // goes instead of respawning, saved as its respawn would leave it.
    [Fact]
    public async Task AMobKillsABodyThatThenGoesWithItsRespawnStateSaved()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableWorldActors = true,
            WorldActorDocument = RealtimeMobAiTests.WolvesAt([6f], damage: 50),
            ItemCatalog = TestCatalog(),
            CombatRules = TestRules(respawnSeconds: 0.5f),
            CombatRandom = new LowestRoll()
        });

        // A development bot watches; it takes no part in combat, so the wolf leaves it alone.
        var observerCharacter = harness.AddCharacter("Watching Bot", isSyntheticBot: true);
        var victim = harness.AddCharacter("Abandoned Hero") with
        {
            SavedState = Saved(harness, 1f, -1f, yaw: 45f, health: 90, mana: 40)
        };
        harness.GameService.AddCharacter(victim);
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        victimClient.SendLeave(victim.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(() => victimClient.Has(RealtimeMessageType.LeaveAccepted), "the leave");

        await harness.PollUntilAsync(
            () => Events(observer).Any(value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == victimEntityId),
            "the wolf to kill the body");
        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == victimEntityId),
            "the dead body to go");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        await harness.PollForAsync(TimeSpan.FromSeconds(1));

        var death = Assert.Single(harness.GameService.PlayerDeaths);
        Assert.Equal(("mob", "mob.feral_wolf"), (death.Killer!.Kind, death.Killer.Id));
        Assert.Equal(LingeringBodyTimer.DiedReason, Despawn(observer, victimEntityId).Value.Reason);
        Assert.DoesNotContain(observer.Despawns, despawn => despawn.Reason == "respawned");
        AssertState(Assert.Single(harness.GameService.Releases).Request.FinalState, 0f, -1f, 0f, 100, 100);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
    }

    // Where the Shard allows player versus player, another player kills a body with the same
    // result.
    [Fact]
    public async Task APlayerKillsABodyThatThenGoesWithItsRespawnStateSaved()
    {
        await using var harness = Create(
            catalog: TestCatalog(minimumDamage: 500, maximumDamage: 500),
            playerVersusPlayer: true);
        var archer = harness.AddCharacter("Opportunist Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var victim = harness.AddCharacter("Defenceless Hero") with
        {
            SavedState = Saved(harness, 2f, -1f, yaw: 90f, health: 55, mana: 5)
        };
        harness.GameService.AddCharacter(victim);
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var victimClient = await harness.JoinAsync(victim);
        var victimEntityId = victimClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => States(archerClient).Any(value => value.EntityId == victimEntityId),
            "the victim to appear");
        victimClient.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(victimEntityId, out _),
            "the body to stay without a connection");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), victimEntityId));
        await harness.PollUntilAsync(
            () => archerClient.Despawns.Any(despawn => despawn.EntityId == victimEntityId),
            "the dead body to go");
        await harness.PollUntilAsync(() => harness.GameService.Releases.Count == 1, "the release of the session");
        await harness.PollForAsync(TimeSpan.FromSeconds(1));

        var death = Assert.Single(harness.GameService.PlayerDeaths);
        Assert.Equal(("character", archer.CharacterId.ToString("D")), (death.Killer!.Kind, death.Killer.Id));
        Assert.Contains(Events(archerClient), value => value.Kind == RealtimeCombatEventKind.Died && value.TargetEntityId == victimEntityId);
        Assert.Equal(LingeringBodyTimer.DiedReason, Despawn(archerClient, victimEntityId).Value.Reason);
        AssertState(Assert.Single(harness.GameService.Releases).Request.FinalState, 0f, -1f, 0f, 100, 100);
    }

    // The player comes back in time: the same entity where the body stood, with the health it had,
    // and its time ends, so the body neither goes nor is swept as an orphan.
    [Fact]
    public async Task APlayerWhoComesBackInTimeResumesTheBody()
    {
        await using var harness = Create(bodySeconds: 2, joinHandshakeTimeout: TimeSpan.FromMilliseconds(300));
        var observerCharacter = harness.AddCharacter("Patient Watcher");
        var character = harness.AddCharacter("Returning Hero") with
        {
            SavedState = Saved(harness, 3.5f, 2.25f, yaw: 90f, health: 37, mana: 12)
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var first = await harness.JoinAsync(character);
        var entityId = first.JoinAccepted!.ControlledEntityId;
        first.SendMoveTo(1, 8f, 2.25f);
        await harness.PollUntilAsync(
            () => first.OwnerSnapshots.Any(snapshot => snapshot.State.PositionX > 3.6f),
            "the step");
        await harness.PollUntilAsync(
            () => observer.Spawns.Any(spawn => spawn.EntityId == entityId),
            "the observer to see the hero");

        first.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");

        // Longer than the orphan sweep waits for a session that no peer owns. The body stopped
        // where its connection ended, which the observer's last snapshot shows.
        await harness.PollForAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(harness.GameService.Releases);
        var body = observer.Snapshots.SelectMany(snapshot => snapshot.Entities).Last(value => value.EntityId == entityId).State;
        Assert.True(body.PositionX > 3.6f);
        var reconnect = character with { Ticket = "ticket-returning-hero", SimulationSessionToken = "returned-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "Returning Hero again");

        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.Equal(entityId, second.JoinAccepted.ControlledEntityId);
        Assert.Equal(
            (body.PositionX, body.PositionY, body.PositionZ),
            (second.JoinAccepted.InitialPlayerState.PositionX, second.JoinAccepted.InitialPlayerState.PositionY, second.JoinAccepted.InitialPlayerState.PositionZ));
        await harness.PollUntilAsync(() => OwnerStates(second).Any(), "the owner combat state");
        Assert.Equal((37, 12), (OwnerStates(second)[^1].Health, OwnerStates(second)[^1].Mana));

        // Past the body's time nothing went, nothing was released, and nobody saw a second entity.
        await harness.PollForAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(harness.GameService.Releases);
        Assert.Equal(2, harness.EntityRegistry.PlayerCount);
        Assert.DoesNotContain(observer.Despawns, despawn => despawn.EntityId == entityId);
        Assert.Single(observer.Spawns, spawn => spawn.PersistentId == character.CharacterId.ToString("D"));
        Assert.Equal("returned-token", harness.SessionStore.ListActiveSessions().Single(session => session.CharacterId == character.CharacterId).SimulationSessionToken);
    }

    // A session that GameService revoked or replaced, or whose lease expired, cannot go on, so its
    // body goes at once and nothing is released.
    [Theory]
    [InlineData("session_revoked")]
    [InlineData("account_session_replaced")]
    [InlineData("session_expired")]
    public async Task ABodyWhoseSessionEndedGoesAtOnce(string code)
    {
        await using var harness = Create();
        var observerCharacter = harness.AddCharacter("Steady Watcher");
        var character = harness.AddCharacter("Revoked Hero") with
        {
            SessionLeaseDurationMilliseconds = code == "session_expired" ? 1_500 : 30_000
        };
        harness.GameService.AddCharacter(character);
        await harness.StartAsync();
        var observer = await harness.JoinAsync(observerCharacter);
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        client.SendLeave(character.SimulationSessionId.ToString("D"));
        await harness.PollUntilAsync(() => client.Has(RealtimeMessageType.LeaveAccepted), "the leave");
        await harness.PollUntilAsync(
            () => observer.Spawns.Any(spawn => spawn.EntityId == entityId),
            "the observer to see the body");

        if (code != "session_expired")
        {
            Assert.True(harness.SessionStore.Invalidate(
                character.CharacterId,
                character.SimulationSessionId,
                character.SimulationSessionToken,
                code,
                "The simulation session ended."));
        }

        await harness.PollUntilAsync(
            () => observer.Despawns.Any(despawn => despawn.EntityId == entityId),
            "the body to go");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(code, Despawn(observer, entityId).Value.Reason);
        Assert.Empty(harness.GameService.Releases);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);
    }

    // A stopping worker saves a body like a connected player in its last checkpoint, and going
    // offline ends its session.
    [Fact]
    public async Task AStoppingWorkerSavesABodyAndTakesItOut()
    {
        var lease = new SimulationWorkerRegistrationLease(TimeProvider.System);
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            CombatRules = TestRules(),
            RegistrationLease = lease
        });
        var observerCharacter = harness.AddCharacter("Last Watcher") with
        {
            SavedState = Saved(harness, -3f, -4f, yaw: 20f, health: 33, mana: 44)
        };
        harness.GameService.AddCharacter(observerCharacter);
        var character = harness.AddCharacter("Sleeping Body") with
        {
            SavedState = Saved(harness, 3f, 4f, yaw: 10f, health: 11, mana: 22)
        };
        harness.GameService.AddCharacter(character);
        using var host = SimulationWorkerStopOrderTests.BuildHost(
            harness,
            lease,
            new ItemCatalogRuntimeDocument(),
            harness.Options.CombatRules!);
        using var timeout = RealtimeServerHarness.CreateTimeout();

        await host.StartAsync(timeout.Token);
        try
        {
            await harness.PollUntilAsync(() => lease.IsValid, "the worker to register");
            await harness.JoinAsync(observerCharacter);
            var client = await harness.JoinAsync(character);
            var entityId = client.JoinAccepted!.ControlledEntityId;
            client.SendLeave(character.SimulationSessionId.ToString("D"));
            await harness.PollUntilAsync(
                () => client.Has(RealtimeMessageType.LeaveAccepted) && !harness.Bindings.TryGetConnectionId(entityId, out _),
                "the body");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.Equal(
            ["checkpoint", "offline", "release", "release"],
            harness.GameService.Calls
                .Select(path => path[(path.LastIndexOf('/') + 1)..])
                .Where(call => call is "checkpoint" or "offline" or "release"));
        var states = Assert.Single(harness.GameService.Checkpoints).States;
        var body = states.Single(state => state.SimulationSessionId == character.SimulationSessionId);
        Assert.Equal((3f, 4f, 10f, 11, 22), (body.PositionX, body.PositionZ, body.YawDegrees, body.Health, body.Mana));
        Assert.Equal(0, harness.EntityRegistry.PlayerCount);
    }

    // The orphan sweep releases a session that neither a peer nor a body owns, and leaves a body's
    // session, which the body's own time releases.
    [Fact]
    public async Task TheOrphanSweepLeavesABodysSessionAndStillReleasesARealOrphan()
    {
        await using var harness = Create(bodySeconds: 3, joinHandshakeTimeout: TimeSpan.FromMilliseconds(300));
        var orphan = RealtimeTestSessions.Create("Orphan Hero");
        Assert.Equal(ActiveSimulationSessionRegistration.Joined, harness.SessionStore.Register(orphan));
        var character = harness.AddCharacter("Lingering Hero");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);
        var entityId = client.JoinAccepted!.ControlledEntityId;
        client.Stop();
        await harness.PollUntilAsync(
            () => !harness.Bindings.TryGetConnectionId(entityId, out _),
            "the body to stay without a connection");

        await harness.PollUntilAsync(
            () => harness.GameService.Releases.Any(release => release.SimulationSessionId == orphan.SimulationSessionId),
            "the orphan's release");
        await harness.PollForAsync(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(harness.GameService.Releases, release => release.SimulationSessionId == character.SimulationSessionId);
        Assert.Equal(1, harness.EntityRegistry.PlayerCount);

        await harness.PollUntilAsync(
            () => harness.GameService.Releases.Any(release => release.SimulationSessionId == character.SimulationSessionId),
            "the body's release");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));
        Assert.Single(harness.GameService.Releases, release => release.SimulationSessionId == character.SimulationSessionId);
        Assert.Equal(0, harness.EntityRegistry.PlayerCount);
    }

    // The timer counts in simulated ticks: a body of 20 seconds goes on the 600th tick, a hit puts
    // its end 600 ticks after the hit, and hits every 5 seconds keep it for minutes.
    [Fact]
    public async Task TheBodyGoesItsFullTimeAfterItsConnectionEndedOrItsLastHit()
    {
        using var fixture = new RealtimeLoopFixture(lingeringBodySeconds: 20);
        await using var service = BodyTimerService.Create(fixture);
        var observer = fixture.ConnectPlayer(1, "Tick Watcher");
        var hero = fixture.ConnectPlayer(2, "Tick Hero");
        SeeEachOther(fixture, observer, hero);
        var heroEntityId = hero.Entity.NetworkEntityId;
        Assert.True(fixture.Teardown.LeaveBody(hero.Context, hero.Session));
        Assert.Equal(1, fixture.Bodies.Count);

        service.RunTicks(599);
        Assert.Equal(2, fixture.Entities.PlayerCount);
        service.RunTicks(1);
        Assert.Equal(1, fixture.Entities.PlayerCount);
        Assert.Equal(600u, fixture.Clock.ServerTick);
        Assert.Equal(LingeringBodyTimer.TimeUpReason, DespawnReason(observer, heroEntityId));

        var hit = fixture.ConnectPlayer(3, "Tick Target");
        fixture.Presence.TrackJoiningEntity(hit.Entity, true);
        Assert.True(fixture.Presence.IsVisibleTo(observer.Context.PeerId, hit.Entity.NetworkEntityId));
        var hitEntityId = hit.Entity.NetworkEntityId;
        Assert.True(fixture.Teardown.LeaveBody(hit.Context, hit.Session));
        var formedAt = fixture.Clock.ServerTick;

        // Hits every 150 ticks for four minutes, then none.
        uint lastHit = 0;
        for (var round = 0; round < 48; round++)
        {
            service.RunTicks(150);
            lastHit = fixture.Clock.ServerTick;
            fixture.Bodies.ExtendAfterHit(hitEntityId, lastHit);
        }

        Assert.True(lastHit - formedAt >= 7_200u);
        service.RunTicks(599);
        Assert.Equal(2, fixture.Entities.PlayerCount);
        service.RunTicks(1);
        Assert.Equal(1, fixture.Entities.PlayerCount);
        Assert.Equal(lastHit + 600u, fixture.Clock.ServerTick);
        Assert.Equal(LingeringBodyTimer.TimeUpReason, DespawnReason(observer, hitEntityId));

        await fixture.OperationTracker.WaitForAllAsync();
        Assert.Equal(
            [hero.Session.SimulationSessionId, hit.Session.SimulationSessionId],
            service.GameService.Releases.Select(release => release.SimulationSessionId));
    }

    // A newer session of the character that joins while the body of an older one still stands
    // replaces it: its viewers see it go, and it is no body any more.
    [Fact]
    public void ANewerSessionReplacesALingeringBodyForItsViewers()
    {
        using var fixture = new RealtimeLoopFixture();
        var observer = fixture.ConnectPlayer(1, "Replacement Watcher");
        var original = fixture.ConnectPlayer(2, "Replaced Hero");
        SeeEachOther(fixture, observer, original);
        Assert.True(fixture.Teardown.LeaveBody(original.Context, original.Session));
        var removed = new List<ulong>();
        fixture.Hooks.AddPlayerRemoved((context, session, entityId) => removed.Add(entityId));

        fixture.Teardown.RemoveReplacedEntity(original.Entity);

        Assert.Equal(0, fixture.Bodies.Count);
        Assert.Equal("entity_replaced", DespawnReason(observer, original.Entity.NetworkEntityId));
        Assert.Equal([original.Entity.NetworkEntityId], removed);
    }

    private static RealtimeServerHarness Create(
        int? bodySeconds = null,
        ItemCatalogRuntimeDocument? catalog = null,
        bool playerVersusPlayer = false,
        TimeSpan? joinHandshakeTimeout = null)
    {
        return RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            // Without regeneration the vitals stay as they were.
            CombatRules = TestRules(),
            ItemCatalog = catalog,
            PlayerVersusPlayer = playerVersusPlayer,
            CombatRandom = new LowestRoll(),
            LingeringBodySeconds = bodySeconds,
            JoinHandshakeTimeout = joinHandshakeTimeout ?? TimeSpan.FromSeconds(2)
        });
    }

    // Puts both players in the interest index and lets the observer see the other.
    private static void SeeEachOther(RealtimeLoopFixture fixture, ConnectedPlayer observer, ConnectedPlayer other)
    {
        fixture.Presence.TrackJoiningEntity(observer.Entity, true);
        fixture.Presence.TrackJoiningEntity(other.Entity, true);
        fixture.Presence.RefreshConnection(observer.Context.PeerId, observer.Entity.NetworkEntityId);
        Assert.True(fixture.Presence.IsVisibleTo(observer.Context.PeerId, other.Entity.NetworkEntityId));
    }

    private static string DespawnReason(ConnectedPlayer observer, ulong entityId)
    {
        return observer.Peer.Sent
            .Where(sent => sent.Type == RealtimeMessageType.EntityDespawn)
            .Select(sent =>
            {
                Assert.True(RealtimeProtocol.TryDecodeEntityDespawn(sent.Data, out var despawn, out var error), error);
                return despawn;
            })
            .Single(despawn => despawn.EntityId == entityId)
            .Reason;
    }

    private static (RecordedRealtimePacket Packet, RealtimeEntityDespawn Value) Despawn(RealtimeRecordingClient client, ulong entityId)
    {
        return client.DecodeWithPackets<RealtimeEntityDespawn>(
                RealtimeMessageType.EntityDespawn,
                RealtimeProtocol.TryDecodeEntityDespawn)
            .Single(value => value.Value.EntityId == entityId);
    }

    private static IReadOnlyList<RecordedRealtimePacket> HitsOn(RealtimeRecordingClient client, ulong entityId)
    {
        return client.DecodeWithPackets<RealtimeCombatEventBatch>(
                RealtimeMessageType.CombatEventBatch,
                RealtimeProtocol.TryDecodeCombatEventBatch)
            .Where(value => value.Value.Events.Any(combatEvent => combatEvent.Kind == RealtimeCombatEventKind.Hit
                && combatEvent.TargetEntityId == entityId))
            .Select(value => value.Packet)
            .ToArray();
    }

    private static int BodySecondsOf(RealtimeRecordingClient client)
    {
        var packet = client.Packets.Single(value => value.Type == RealtimeMessageType.LeaveAccepted);
        Assert.True(RealtimeProtocol.TryDecodeLeaveAccepted(packet.Data, out var seconds, out var error), error);
        return seconds;
    }

    // A state that GameService saved on the ground of the harness World with its collision.
    private static SimulationSavedCharacterState Saved(
        RealtimeServerHarness harness,
        float x,
        float z,
        float yaw,
        int health,
        int mana)
    {
        return new SimulationSavedCharacterState(
            RealtimeServerHarness.WorldId,
            harness.StaticCollisionWorld.Revision,
            x,
            0f,
            z,
            yaw,
            health,
            mana);
    }

    private static void AssertState(
        SimulationCharacterStateRequest? state,
        float x,
        float z,
        float yaw,
        int health,
        int mana)
    {
        Assert.NotNull(state);
        Assert.True(state.Sequence > 0);
        Assert.Equal((x, 0f, z, yaw, health, mana), (state.PositionX, state.PositionY, state.PositionZ, state.YawDegrees, state.Health, state.Mana));
    }

    // The body timer of a loop fixture, driven tick by tick, with a scripted GameService for the
    // releases it starts.
    private sealed class BodyTimerService : IAsyncDisposable
    {
        private readonly HttpClient httpClient;
        private readonly RealtimePerformanceMetrics metrics = new();
        private readonly SimulationTickLoop loop;
        private TimeSpan nextTick = TimeSpan.Zero;

        private BodyTimerService(RealtimeLoopFixture fixture)
        {
            httpClient = new HttpClient(GameService) { BaseAddress = new Uri("http://game-service.test") };
            var client = new GameServiceClient(httpClient);
            var operations = new SessionAuthorityOperations(
                new SimulationJoinService(
                    client,
                    fixture.SessionStore,
                    fixture.CharacterItemStateStore,
                    fixture.Config,
                    new SimulationWorkerIdentity(RealtimeServerHarness.RuntimeId, DateTime.UtcNow)),
                new SimulationSessionReleaseService(
                    client,
                    fixture.SessionStore,
                    CharacterStateTestData.SessionStores(fixture.CharacterItemStateStore)),
                fixture.Completions,
                fixture.OperationTracker,
                NullLogger.Instance);
            var timer = new LingeringBodyTimer(
                fixture.Bodies,
                fixture.SessionStore,
                fixture.Teardown,
                operations,
                null,
                NullLogger.Instance);
            var phases = new SimulationTickPhases();
            timer.AddTickPhase(phases);
            loop = new SimulationTickLoop(fixture.Clock, fixture.Entities, phases, metrics, NullLogger.Instance);
        }

        public ScriptedGameServiceHandler GameService { get; } = new();

        public static BodyTimerService Create(RealtimeLoopFixture fixture)
        {
            return new BodyTimerService(fixture);
        }

        // One due tick per call, so the loop never resynchronizes.
        public void RunTicks(int count)
        {
            var interval = TimeSpan.FromSeconds(1d / TickRateHz);
            for (var tick = 0; tick < count; tick++)
            {
                loop.ProcessDueTicks(nextTick, interval, ref nextTick);
            }
        }

        public ValueTask DisposeAsync()
        {
            metrics.Dispose();
            httpClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
