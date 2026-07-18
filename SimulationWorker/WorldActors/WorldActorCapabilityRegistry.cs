using ShooterMmo.GameProtocol;
using ShooterMmo.WorldData.Actors;
using SimulationWorker.Sessions;

namespace SimulationWorker.WorldActors;

public interface IWorldActorCapabilityAvailabilityPolicy
{
    bool IsAvailable(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor,
        WorldActorCapabilityDefinition capability);
}

public sealed class DefaultWorldActorCapabilityAvailabilityPolicy
    : IWorldActorCapabilityAvailabilityPolicy
{
    public bool IsAvailable(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor,
        WorldActorCapabilityDefinition capability)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(capability);
        return actor.IsActive;
    }
}

public sealed record WorldActorCapabilitySummary(
    long Revision,
    RealtimeWorldActorCapability[] Capabilities);

public sealed record WorldActorCapabilityOperationContext(
    ActiveSimulationSession Session,
    WorldActorRuntimeState Actor,
    WorldActorCapabilityDefinition Capability,
    RealtimeWorldInteractionIntent Intent);

public sealed record WorldActorCapabilityDispatchResult(
    bool Succeeded,
    string Code,
    string Message,
    long ItemStateRevision = 0,
    bool ShouldDisconnect = false);

public interface IWorldActorCapabilityHandler
{
    RealtimeWorldActorCapabilityKind Kind { get; }

    Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken);
}

public sealed class DeferredWorldActorCapabilityHandler(
    RealtimeWorldActorCapabilityKind kind) : IWorldActorCapabilityHandler
{
    public RealtimeWorldActorCapabilityKind Kind { get; } = kind;

    public Task<WorldActorCapabilityDispatchResult> ExecuteAsync(
        WorldActorCapabilityOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(new WorldActorCapabilityDispatchResult(
            false,
            "world_interaction_capability_deferred",
            "This capability handler is intentionally deferred beyond Phase 13."));
    }
}

public sealed class WorldActorCapabilityRegistry
{
    private readonly IWorldActorCapabilityAvailabilityPolicy availabilityPolicy;
    private readonly Dictionary<RealtimeWorldActorCapabilityKind, IWorldActorCapabilityHandler>
        handlers;

    public WorldActorCapabilityRegistry(
        IWorldActorCapabilityAvailabilityPolicy availabilityPolicy)
        : this(availabilityPolicy, Array.Empty<IWorldActorCapabilityHandler>())
    {
    }

    public WorldActorCapabilityRegistry(
        IWorldActorCapabilityAvailabilityPolicy availabilityPolicy,
        IEnumerable<IWorldActorCapabilityHandler> registeredHandlers)
    {
        this.availabilityPolicy = availabilityPolicy
            ?? throw new ArgumentNullException(nameof(availabilityPolicy));
        handlers = Enum.GetValues<RealtimeWorldActorCapabilityKind>()
            .ToDictionary(
                kind => kind,
                kind => (IWorldActorCapabilityHandler)
                    new DeferredWorldActorCapabilityHandler(kind));
        var explicitlyRegisteredKinds = new HashSet<RealtimeWorldActorCapabilityKind>();
        foreach (var handler in registeredHandlers
                     ?? throw new ArgumentNullException(nameof(registeredHandlers)))
        {
            if (handler == null
                || !Enum.IsDefined(handler.Kind))
            {
                throw new InvalidOperationException(
                    "World actor capability handlers must use a supported capability kind.");
            }

            if (!explicitlyRegisteredKinds.Add(handler.Kind))
            {
                throw new InvalidOperationException(
                    "Only one world actor capability handler may be registered per kind.");
            }

            handlers[handler.Kind] = handler;
        }
    }

    public IReadOnlyCollection<RealtimeWorldActorCapabilityKind> RegisteredKinds =>
        handlers.Keys.OrderBy(kind => kind).ToArray();

    public WorldActorCapabilitySummary BuildSummary(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(actor);
        var capabilities = actor.Definition.Capabilities
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .Select((capability, index) => new RealtimeWorldActorCapability(
                capability.Id,
                ToProtocolKind(capability.Kind),
                capability.DisplayName,
                availabilityPolicy.IsAvailable(session, actor, capability),
                checked(actor.InteractionRevision + index)))
            .ToArray();
        return new WorldActorCapabilitySummary(
            ComputeSummaryRevision(session, actor, capabilities),
            capabilities);
    }

    public Task<WorldActorCapabilityDispatchResult> DispatchAsync(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor,
        WorldActorCapabilityDefinition capability,
        RealtimeWorldInteractionIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(capability);
        var kind = ToProtocolKind(capability.Kind);
        if (!handlers.TryGetValue(kind, out var handler))
        {
            return Task.FromResult(new WorldActorCapabilityDispatchResult(
                false,
                "world_interaction_capability_unavailable",
                "No authoritative handler is registered for this capability."));
        }

        return handler.ExecuteAsync(new WorldActorCapabilityOperationContext(
            session,
            actor,
            capability,
            intent), cancellationToken);
    }

    private static long ComputeSummaryRevision(
        ActiveSimulationSession session,
        WorldActorRuntimeState actor,
        IReadOnlyList<RealtimeWorldActorCapability> capabilities)
    {
        var hash = 1469598103934665603UL;
        foreach (var value in session.CharacterId.ToByteArray())
        {
            hash = (hash ^ value) * 1099511628211UL;
        }

        hash = (hash ^ (ulong)actor.InteractionRevision) * 1099511628211UL;
        for (var index = 0; index < capabilities.Count; index++)
        {
            foreach (var character in capabilities[index].Id)
            {
                hash = (hash ^ character) * 1099511628211UL;
            }

            hash = (hash ^ (capabilities[index].IsAvailable ? 1UL : 0UL))
                * 1099511628211UL;
        }

        return (long)(hash % long.MaxValue) + 1;
    }

    internal static RealtimeWorldActorCapabilityKind ToProtocolKind(string kind)
    {
        return kind switch
        {
            WorldActorCapabilityKindIds.Dialogue => RealtimeWorldActorCapabilityKind.Dialogue,
            WorldActorCapabilityKindIds.Vendor => RealtimeWorldActorCapabilityKind.Vendor,
            WorldActorCapabilityKindIds.QuestOffer => RealtimeWorldActorCapabilityKind.QuestOffer,
            WorldActorCapabilityKindIds.QuestTurnIn => RealtimeWorldActorCapabilityKind.QuestTurnIn,
            WorldActorCapabilityKindIds.Crafting => RealtimeWorldActorCapabilityKind.Crafting,
            WorldActorCapabilityKindIds.Insurance => RealtimeWorldActorCapabilityKind.Insurance,
            WorldActorCapabilityKindIds.Trainer => RealtimeWorldActorCapabilityKind.Trainer,
            WorldActorCapabilityKindIds.Bank => RealtimeWorldActorCapabilityKind.Bank,
            WorldActorCapabilityKindIds.RecoveryStorage => RealtimeWorldActorCapabilityKind.RecoveryStorage,
            _ => throw new InvalidOperationException(
                "Compiled world actor capability kind is unsupported: " + kind)
        };
    }
}
