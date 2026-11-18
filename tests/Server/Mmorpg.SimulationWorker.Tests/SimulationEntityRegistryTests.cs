using Microsoft.Extensions.Logging;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.Tests.Realtime;
using Mmorpg.Testing.Content;

namespace Mmorpg.SimulationWorker.Tests;

public sealed class SimulationEntityRegistryTests
{
    private static readonly MovementSimulationSettings MovementSettings = new(
        30,
        5f,
        720f,
        -24f,
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
    public void EntityIdsStopAtTheLargestIdThatASnapshotRecordCarries()
    {
        var registry = new SimulationEntityRegistry(uint.MaxValue);

        Assert.Equal(uint.MaxValue, registry.AllocateNetworkEntityId());
        var exception = Assert.Throws<InvalidOperationException>(() => registry.AllocateNetworkEntityId());
        Assert.Contains("exhausted", exception.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPlayer(CreateSession(), CreateMovement));
    }

    // The gauge of the ids used reads this number: every id given out counts, for players and
    // for anything else that takes one, and an id is not given back when its entity leaves.
    [Fact]
    public void TheIssuedEntityIdsCountEveryIdThatWasGivenOutAndNeverShrink()
    {
        var registry = new SimulationEntityRegistry();
        Assert.Equal(0, registry.IssuedEntityIds);

        var first = registry.RegisterPlayer(CreateSession(), CreateMovement);
        registry.AllocateNetworkEntityId();
        Assert.Equal(2, registry.IssuedEntityIds);

        Assert.True(registry.RemovePlayer(first.Entity.NetworkEntityId, first.Entity.Session.SimulationSessionId, out _));
        Assert.Equal(2, registry.IssuedEntityIds);

        var last = new SimulationEntityRegistry(uint.MaxValue);
        Assert.Equal(uint.MaxValue - 1L, last.IssuedEntityIds);
        last.AllocateNetworkEntityId();
        Assert.Equal(uint.MaxValue, last.IssuedEntityIds);
        Assert.Throws<InvalidOperationException>(() => last.AllocateNetworkEntityId());
        Assert.Equal(uint.MaxValue, last.IssuedEntityIds);
    }

    // The worker that used every id says so, clearly and not once per failed join: a critical
    // line with the restart that an operator needs, at most once a minute.
    [Fact]
    public void AnExhaustedCounterLogsOneClearCriticalLineInsteadOfOneForEveryFailedAllocation()
    {
        var logger = new CapturingLogger<SimulationEntityRegistry>();
        var registry = new SimulationEntityRegistry(uint.MaxValue, logger);
        Assert.Equal(uint.MaxValue, registry.AllocateNetworkEntityId());
        Assert.Empty(logger.Entries);

        Assert.Throws<InvalidOperationException>(() => registry.AllocateNetworkEntityId());
        Assert.Throws<InvalidOperationException>(() => registry.RegisterPlayer(CreateSession(), CreateMovement));
        Assert.Throws<InvalidOperationException>(() => registry.AllocateNetworkEntityId());

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, line.Level);
        Assert.Contains(
            "All 4294967295 simulation entity ids are used, so no player can join and no Mob can respawn",
            line.Message,
            StringComparison.Ordinal);
        Assert.Contains("Restart the worker", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectPreservesEntityIdAndMovementForTheSameSimulationSession()
    {
        var registry = new SimulationEntityRegistry();
        var originalSession = CreateSession();
        var original = registry.RegisterPlayer(originalSession, CreateMovement);
        original.Entity.Movement.AcceptInputs(new RealtimeMovementInputBatch(
            [new RealtimeMovementInput(1, 1, 1)],
            new RealtimeMovementPath(1, [new RealtimeMovementPathWaypoint(0f, 0f, 5f)])));
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
            "development-world-1",
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
