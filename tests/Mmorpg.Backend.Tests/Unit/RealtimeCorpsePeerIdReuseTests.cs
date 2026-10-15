using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Corpses;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Items;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;
using Mmorpg.SimulationWorker.WorldActors;

namespace Mmorpg.Backend.Tests.Unit;

// LiteNetLib hands a freed peer id to the next connection. A corpse completion of the
// disconnected peer must not release the corpse view or lease of the new peer (F56).
public sealed class RealtimeCorpsePeerIdReuseTests
{
    private const int ReusedPeerId = 5;

    [Fact]
    public void StaleCompletionDoesNotCloseTheViewOfThePeerThatReusedItsId()
    {
        var fixture = new FinalizerFixture();
        var corpseId = Guid.NewGuid();
        var staleContext = fixture.Connect(ReusedPeerId);
        Assert.True(fixture.CorpseViewers.TryOpen(ReusedPeerId, corpseId, out _, out _));
        Assert.True(fixture.Leases.TryAcquireCorpse(ReusedPeerId, corpseId, out _, out _, out _));

        // The old peer disconnects while its open is in flight; a new peer takes the id.
        Assert.True(fixture.Registry.Remove(ReusedPeerId, out _));
        fixture.CorpseViewers.RemovePeer(ReusedPeerId);
        fixture.Leases.RemovePeer(ReusedPeerId);
        var currentContext = fixture.Connect(ReusedPeerId);
        Assert.True(fixture.CorpseViewers.TryOpen(ReusedPeerId, corpseId, out _, out _));
        Assert.True(fixture.Leases.TryAcquireCorpse(ReusedPeerId, corpseId, out _, out _, out _));

        fixture.Finalizer.Complete(new CorpseOperationCompleted(
            staleContext,
            staleContext.Session!,
            SimulationCorpseInteractionResult.Rejected(
                RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId),
                "corpse_operation_failed",
                "SimulationWorker could not complete the corpse operation.",
                requiresCorpseRefresh: true),
            RemoveOpenReservationOnFailure: true));

        Assert.True(fixture.CorpseViewers.IsViewing(ReusedPeerId, corpseId));
        Assert.True(fixture.Leases.TryGet(ReusedPeerId, out var lease));
        Assert.Equal(corpseId, lease!.TargetRuntimeId);
        Assert.Empty(fixture.Peers[ReusedPeerId].Sent);
        Assert.False(staleContext.AuthorityOperationInFlight);
        Assert.Null(currentContext.DisconnectAfterUtc);
    }

    [Fact]
    public void StaleCompletionOfThePeerThatStillOwnsItsIdReleasesItsReservation()
    {
        var fixture = new FinalizerFixture();
        var corpseId = Guid.NewGuid();
        var context = fixture.Connect(ReusedPeerId);
        var openedSession = context.Session!;
        Assert.True(fixture.CorpseViewers.TryOpen(ReusedPeerId, corpseId, out _, out _));
        Assert.True(fixture.Leases.TryAcquireCorpse(ReusedPeerId, corpseId, out _, out _, out _));

        // The session changed while the open was in flight, so the fence rejects it.
        context.Session = RealtimeTestSessions.Create("Replacement Session");
        fixture.Finalizer.Complete(new CorpseOperationCompleted(
            context,
            openedSession,
            SimulationCorpseInteractionResult.Rejected(
                RealtimeCorpseInteractionIntent.CreateOpen(Guid.NewGuid(), corpseId),
                "corpse_operation_failed",
                "SimulationWorker could not complete the corpse operation."),
            RemoveOpenReservationOnFailure: true));

        Assert.False(fixture.CorpseViewers.IsViewing(ReusedPeerId, corpseId));
        Assert.False(fixture.Leases.TryGet(ReusedPeerId, out _));
        Assert.Empty(fixture.Peers[ReusedPeerId].Sent);
    }

    private sealed class FinalizerFixture
    {
        private readonly RecordingPeerDirectory directory = new();

        public FinalizerFixture()
        {
            var bindings = new ConnectionEntityBindingRegistry();
            var entities = new SimulationEntityRegistry();
            var sessionStore = new ActiveSimulationSessionStore();
            var carryStateStore = new CarryStateStore();
            var corpseStore = new DurableCorpseStore(TimeProvider.System);
            Registry = new RealtimePeerRegistry(directory, bindings, entities, Metrics);
            var sender = new RealtimePeerSender(Metrics, NullLogger.Instance);
            var presence = new EntityPresenceReplicator(
                Registry,
                sender,
                entities,
                bindings,
                null,
                new SimulationInterestManager(InterestManagementConfig.Default),
                Metrics,
                new RealtimeSimulationClock());
            var carrySync = new CarryStateSynchronizer(carryStateStore, bindings, Registry, sender);
            var teardown = new RealtimeSessionTeardown(
                Registry,
                sender,
                presence,
                sessionStore,
                carryStateStore,
                CorpseViewers,
                Leases,
                bindings,
                entities,
                Metrics);
            Finalizer = new CorpseOperationFinalizer(
                Registry,
                sender,
                sessionStore,
                carryStateStore,
                entities,
                carrySync,
                teardown,
                new AuthorityOperationScheduler(Registry),
                CorpseViewers,
                Leases,
                new CorpseViewDistributor(
                    CorpseViewers,
                    Leases,
                    Registry,
                    sender,
                    corpseStore,
                    null),
                corpseStore,
                null);
        }

        public RealtimeNetworkMetrics Metrics { get; } = new();

        public CorpseViewerRegistry CorpseViewers { get; } = new();

        public WorldInteractionLeaseRegistry Leases { get; } = new();

        public RealtimePeerRegistry Registry { get; }

        public CorpseOperationFinalizer Finalizer { get; }

        public IReadOnlyDictionary<int, RecordingPeerConnection> Peers => directory.Peers;

        public RealtimePeerContext Connect(int peerId)
        {
            directory.Peers[peerId] = new RecordingPeerConnection(peerId);
            var context = new RealtimePeerContext(peerId, DateTime.UtcNow, UdpQuotaConfig.Default)
            {
                Session = RealtimeTestSessions.Create(),
                AuthorityOperationInFlight = true
            };
            Registry.Register(context);
            return context;
        }
    }
}
