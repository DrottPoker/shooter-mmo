using Microsoft.Extensions.Configuration;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using static Mmorpg.SimulationWorker.Tests.Realtime.RealtimeCombatTests;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// The development admin tools: a worker that allows them tells every joined player its admin
// state and changes the walk speed, teleports, fills health, and makes a player invulnerable;
// any other worker refuses them.
public sealed class RealtimeAdminTests
{
    [Fact]
    public async Task TheOwnerLearnsItsStateAndWalksAtTheChosenSpeedAfterAReconnectToo()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableAdminTools = true });
        var character = harness.AddCharacter("Fast Walker");
        await harness.StartAsync();
        var first = await harness.JoinAsync(character);
        await harness.PollUntilAsync(() => AdminStates(first).Count == 1, "the joined admin state");
        Assert.Equal((100, false), Describe(AdminStates(first)[0]));

        var intent = RealtimeAdminIntent.CreateSetMovementSpeed(Guid.NewGuid(), 300);
        first.Send(RealtimeProtocol.EncodeAdminIntent(intent));
        await harness.PollUntilAsync(() => AdminStates(first).Count == 2, "the faster admin state");
        var result = Assert.Single(Results(first));
        Assert.True(result.Succeeded);
        Assert.Equal(intent.OperationId, result.OperationId);
        Assert.Equal(RealtimeAdminIntentKind.SetMovementSpeed, result.Kind);
        Assert.Equal((300, false), Describe(AdminStates(first)[1]));

        first.SendMoveTo(1, 0f, 12f);
        await harness.PollUntilAsync(
            () => first.OwnerSnapshots.Any(snapshot => snapshot.State.VelocityZ > 1f),
            "the character to walk");
        Assert.Equal(15f, first.OwnerSnapshots.Last(snapshot => snapshot.State.VelocityZ > 1f).State.VelocityZ, 3);

        // The same session reconnects and keeps its entity, so it keeps its speed.
        var reconnect = character with { Ticket = "ticket-fast-reconnect", SimulationSessionToken = "fast-reconnected-token" };
        harness.GameService.AddCharacter(reconnect);
        var second = await harness.JoinAsync(reconnect, "second");
        await harness.PollUntilAsync(() => AdminStates(second).Count == 1, "the reconnected admin state");
        Assert.True(second.JoinAccepted!.IsReconnect);
        Assert.Equal((300, false), Describe(AdminStates(second)[0]));
    }

    [Fact]
    public async Task ATeleportMovesTheOwnerOntoTheGroundAndRespawnsItForViewers()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions { EnableAdminTools = true });
        var traveller = harness.AddCharacter("Traveller");
        var viewer = harness.AddCharacter("Teleport Viewer");
        await harness.StartAsync();
        var owner = await harness.JoinAsync(traveller);
        var watcher = await harness.JoinAsync(viewer);
        var entityId = owner.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(() => watcher.Spawns.Any(spawn => spawn.EntityId == entityId), "the traveller's spawn");

        var teleport = RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), 8f, 0.05f, 6f);
        owner.Send(RealtimeProtocol.EncodeAdminIntent(teleport));
        await harness.PollUntilAsync(
            () => Results(owner).Count == 1
                && watcher.Despawns.Any(despawn => despawn.EntityId == entityId && despawn.Reason == "teleported")
                && watcher.Spawns.Count(spawn => spawn.EntityId == entityId) == 2
                && owner.OwnerSnapshots.Any(snapshot => Math.Abs(snapshot.State.PositionX - 8f) < 0.001f),
            "the teleport");

        Assert.True(Assert.Single(Results(owner)).Succeeded);
        var arrived = owner.OwnerSnapshots.Last().State;
        Assert.Equal(8f, arrived.PositionX, 3);
        Assert.Equal(0f, arrived.PositionY, 3);
        Assert.Equal(6f, arrived.PositionZ, 3);
        var respawned = watcher.Spawns.Last(spawn => spawn.EntityId == entityId).InitialState;
        Assert.Equal(8f, respawned.PositionX, 3);
        Assert.Equal(6f, respawned.PositionZ, 3);

        owner.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), 20f, 0f, 0f)));
        owner.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateTeleport(Guid.NewGuid(), 2f, 10f, 2f)));
        await harness.PollUntilAsync(() => Results(owner).Count == 3, "the refused teleports");
        Assert.Equal(
            ["admin_teleport_outside_world", "admin_teleport_no_ground"],
            Results(owner).Skip(1).Select(result => result.Error!.Code));
        Assert.Equal(8f, owner.OwnerSnapshots.Last().State.PositionX, 3);
    }

    [Fact]
    public async Task RestoringHealthFillsItAndAnInvulnerablePlayerKeepsIt()
    {
        await using var harness = RealtimeServerHarness.Create(new RealtimeServerHarnessOptions
        {
            EnableAdminTools = true,
            ItemCatalog = TestCatalog(minimumDamage: 10, maximumDamage: 10),
            CombatRules = TestRules(),
            PlayerVersusPlayer = true,
            CombatRandom = new LowestRoll()
        });
        var archer = harness.AddCharacter("Admin Archer") with { Equipment = ArcherEquipment(50) };
        harness.GameService.AddCharacter(archer);
        var target = harness.AddCharacter("Admin Target");
        await harness.StartAsync();
        var archerClient = await harness.JoinAsync(archer);
        var targetClient = await harness.JoinAsync(target);
        var targetEntityId = targetClient.JoinAccepted!.ControlledEntityId;
        await harness.PollUntilAsync(
            () => RealtimeCombatTests.States(archerClient).Any(value => value.EntityId == targetEntityId),
            "the target to appear");

        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(() => Hits(targetClient, targetEntityId) > 0, "a hit");
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateClearTarget(Guid.NewGuid()));
        await harness.PollForAsync(TimeSpan.FromMilliseconds(700));
        Assert.True(Health(archerClient, targetEntityId) < 100);

        targetClient.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateRestoreHealth(Guid.NewGuid())));
        await harness.PollUntilAsync(() => Health(archerClient, targetEntityId) == 100, "full health");
        Assert.True(Assert.Single(Results(targetClient)).Succeeded);

        targetClient.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetInvulnerable(Guid.NewGuid(), true)));
        await harness.PollUntilAsync(() => AdminStates(targetClient).Count == 2, "the invulnerable admin state");
        Assert.Equal((100, true), Describe(AdminStates(targetClient)[1]));
        var hitsBefore = Hits(targetClient, targetEntityId);
        var releasesBefore = Releases(targetClient, targetEntityId);
        archerClient.SendCombatIntent(RealtimeCombatIntent.CreateAttackTarget(Guid.NewGuid(), targetEntityId));
        await harness.PollUntilAsync(() => Releases(targetClient, targetEntityId) >= releasesBefore + 2, "two more arrows");
        await harness.PollForAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(hitsBefore, Hits(targetClient, targetEntityId));
        Assert.Equal(100, Health(archerClient, targetEntityId));
    }

    [Fact]
    public async Task AWorkerWithoutTheToolsSendsNoStateAndRefusesThem()
    {
        await using var harness = RealtimeServerHarness.Create();
        var character = harness.AddCharacter("Plain Walker");
        await harness.StartAsync();
        var client = await harness.JoinAsync(character);

        client.Send(RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetMovementSpeed(Guid.NewGuid(), 1000)));
        await harness.PollUntilAsync(() => Results(client).Count == 1, "the refusal");
        client.SendMoveTo(1, 0f, 12f);
        await harness.PollUntilAsync(
            () => client.OwnerSnapshots.Any(snapshot => snapshot.State.VelocityZ > 1f),
            "the character to walk");

        var result = Assert.Single(Results(client));
        Assert.False(result.Succeeded);
        Assert.Equal("admin_tools_disabled", result.Error!.Code);
        Assert.Empty(AdminStates(client));
        Assert.Equal(5f, client.OwnerSnapshots.Last(snapshot => snapshot.State.VelocityZ > 1f).State.VelocityZ, 3);
    }

    [Fact]
    public void AdminToolsAreExplicitAndEnvironmentGuarded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentAdmin:Enabled"] = "true"
            })
            .Build();

        var productionError = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentAdminOptions.FromConfiguration(configuration, isDevelopment: false));
        Assert.Contains("can only be true in the Development environment", productionError.Message);
        Assert.True(DevelopmentAdminOptions.FromConfiguration(configuration, isDevelopment: true).Enabled);
        Assert.False(DevelopmentAdminOptions.FromConfiguration(
            new ConfigurationBuilder().Build(),
            isDevelopment: true).Enabled);
    }

    private static IReadOnlyList<RealtimeAdminIntentResult> Results(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAdminIntentResult>(
            RealtimeMessageType.AdminIntentResult,
            RealtimeProtocol.TryDecodeAdminIntentResult);
    }

    private static IReadOnlyList<RealtimeAdminStateChanged> AdminStates(RealtimeRecordingClient client)
    {
        return client.Decode<RealtimeAdminStateChanged>(
            RealtimeMessageType.AdminStateChanged,
            RealtimeProtocol.TryDecodeAdminStateChanged);
    }

    private static (int SpeedPercent, bool Invulnerable) Describe(RealtimeAdminStateChanged state)
    {
        return (state.SpeedPercent, state.Invulnerable);
    }

    private static int Hits(RealtimeRecordingClient client, ulong targetEntityId)
    {
        return Events(client).Count(value => value.Kind == RealtimeCombatEventKind.Hit && value.TargetEntityId == targetEntityId);
    }

    private static int Releases(RealtimeRecordingClient client, ulong targetEntityId)
    {
        return Events(client).Count(value => value.Kind == RealtimeCombatEventKind.AttackReleased && value.TargetEntityId == targetEntityId);
    }

    private static int Health(RealtimeRecordingClient client, ulong entityId)
    {
        return RealtimeCombatTests.States(client).Last(value => value.EntityId == entityId).Health;
    }
}
