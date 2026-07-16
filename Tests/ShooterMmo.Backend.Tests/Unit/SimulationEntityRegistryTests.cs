using ShooterMmo.GameProtocol;
using ShooterMmo.GameSimulation;
using SimulationWorker.Entities;
using SimulationWorker.Realtime;
using SimulationWorker.Sessions;

namespace ShooterMmo.Backend.Tests.Unit;

public sealed class SimulationEntityRegistryTests
{
    private static readonly MovementSimulationSettings MovementSettings = new(
        30,
        5f,
        8f,
        720f,
        -24f,
        7f,
        -2f,
        0f,
        -14f,
        14f,
        -14f,
        14f);

    [Fact]
    public void NewPlayersReceiveMonotonicNonZeroNetworkEntityIds()
    {
        var registry = new SimulationEntityRegistry();

        var first = registry.RegisterPlayer(CreateSession(), CreateMovement);
        var second = registry.RegisterPlayer(CreateSession(), CreateMovement);

        Assert.Equal(1ul, first.Entity.NetworkEntityId);
        Assert.Equal(2ul, second.Entity.NetworkEntityId);
        Assert.True(first.IsNewEntity);
        Assert.True(second.IsNewEntity);
        Assert.Equal(2, registry.PlayerCount);
        var copied = new List<PlayerSimulationEntity>();
        registry.CopyPlayersTo(copied);
        Assert.Equal(
            [first.Entity.NetworkEntityId, second.Entity.NetworkEntityId],
            copied.Select(entity => entity.NetworkEntityId));
    }

    [Fact]
    public void ReconnectPreservesEntityIdAndMovementForTheSameSimulationSession()
    {
        var registry = new SimulationEntityRegistry();
        var originalSession = CreateSession();
        var original = registry.RegisterPlayer(originalSession, CreateMovement);
        original.Entity.Movement.AcceptInputs(
        [
            new RealtimeMovementInput(
                1,
                1,
                0f,
                1f,
                0f,
                RealtimeMovementButtons.None)
        ]);
        original.Entity.Movement.SimulateTick();
        var refreshedSession = originalSession with
        {
            SimulationSessionToken = "rotated-session-token",
            SessionExpiresAt = originalSession.SessionExpiresAt.AddMinutes(1),
            IsReconnect = true
        };
        var movementFactoryCalls = 0;

        var reconnect = registry.RegisterPlayer(
            refreshedSession,
            () =>
            {
                movementFactoryCalls++;
                return CreateMovement();
            });

        Assert.False(reconnect.IsNewEntity);
        Assert.Null(reconnect.ReplacedEntity);
        Assert.Same(original.Entity, reconnect.Entity);
        Assert.Equal(original.Entity.NetworkEntityId, reconnect.Entity.NetworkEntityId);
        Assert.Equal("rotated-session-token", reconnect.Entity.Session.SimulationSessionToken);
        Assert.Equal(1u, reconnect.Entity.Movement.LastProcessedInputSequence);
        Assert.Equal(0, movementFactoryCalls);
    }

    [Fact]
    public void NewSimulationSessionReplacesTheCharactersPreviousEntity()
    {
        var registry = new SimulationEntityRegistry();
        var originalSession = CreateSession();
        var original = registry.RegisterPlayer(originalSession, CreateMovement);
        var replacementSession = originalSession with
        {
            SimulationSessionId = Guid.NewGuid(),
            SimulationSessionToken = "replacement-session-token"
        };

        var replacement = registry.RegisterPlayer(replacementSession, CreateMovement);

        Assert.True(replacement.IsNewEntity);
        Assert.Same(original.Entity, replacement.ReplacedEntity);
        Assert.NotEqual(original.Entity.NetworkEntityId, replacement.Entity.NetworkEntityId);
        Assert.False(registry.TryGetPlayer(original.Entity.NetworkEntityId, out _));
        Assert.True(registry.TryGetPlayer(replacement.Entity.NetworkEntityId, out var current));
        Assert.Same(replacement.Entity, current);
    }

    [Fact]
    public void FailedReplacementConstructionPreservesTheCurrentEntity()
    {
        var registry = new SimulationEntityRegistry();
        var originalSession = CreateSession();
        var original = registry.RegisterPlayer(originalSession, CreateMovement);
        var replacementSession = originalSession with
        {
            SimulationSessionId = Guid.NewGuid(),
            SimulationSessionToken = "replacement-session-token"
        };

        Assert.Throws<InvalidOperationException>(() => registry.RegisterPlayer(
            replacementSession,
            () => throw new InvalidOperationException("Movement construction failed.")));

        Assert.True(registry.TryGetPlayer(original.Entity.NetworkEntityId, out var current));
        Assert.Same(original.Entity, current);
        Assert.Single(registry.ListPlayers());
    }

    [Fact]
    public void RemoveRequiresTheExactSimulationSession()
    {
        var registry = new SimulationEntityRegistry();
        var registration = registry.RegisterPlayer(CreateSession(), CreateMovement);

        Assert.False(registry.RemovePlayer(
            registration.Entity.NetworkEntityId,
            Guid.NewGuid(),
            out _));
        Assert.True(registry.RemovePlayer(
            registration.Entity.NetworkEntityId,
            registration.Entity.Session.SimulationSessionId,
            out var removed));
        Assert.Same(registration.Entity, removed);
        Assert.Empty(registry.ListPlayers());
    }

    [Fact]
    public void ConnectionBindingMaintainsOneToOneOwnership()
    {
        var bindings = new ConnectionEntityBindingRegistry();

        bindings.Bind(10, 100);
        var replacement = bindings.Bind(11, 100);

        Assert.Equal(10, replacement.ReplacedConnectionId);
        Assert.False(bindings.TryGetEntityId(10, out _));
        Assert.True(bindings.TryGetEntityId(11, out var entityId));
        Assert.Equal(100ul, entityId);
        Assert.True(bindings.TryGetConnectionId(100, out var connectionId));
        Assert.Equal(11, connectionId);
        Assert.False(bindings.UnbindConnection(10, out _));
        Assert.True(bindings.UnbindEntity(100, out var removedConnectionId));
        Assert.Equal(11, removedConnectionId);
    }

    private static ActiveSimulationSession CreateSession()
    {
        return new ActiveSimulationSession(
            Guid.NewGuid(),
            "simulation-session-token",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Registry Hero",
            "local-shard-1",
            "local-world-1",
            "local-simulation-worker-1",
            "worker-runtime-1",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            PlayerCarryState.Default,
            false);
    }

    private static AuthoritativePlayerMovement CreateMovement()
    {
        var collisionWorld = CollisionTestWorldFactory.Create();
        return new AuthoritativePlayerMovement(
            PlayerMovementSimulation.CreateInitialState(
                MovementSettings,
                collisionWorld,
                0f,
                0f,
                -1f,
                0f),
            MovementSettings,
            PlayerCarryState.Default,
            collisionWorld);
    }
}
