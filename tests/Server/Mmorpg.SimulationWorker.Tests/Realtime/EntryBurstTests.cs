using LiteNetLib;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.WorldData.Items;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

// A crowd that enters a connection's view must not fill the connection's reliable queues beyond
// the limit that disconnects it, on a line that keeps up and on one that does not. The real loop
// runs tick by tick around recording peers, and the queue of a peer is modelled as LiteNetLib keeps
// it: what a tick sends waits in the outgoing queue, the peer supervisor looks at it right after
// the tick, before the transport moves anything into the window of 64 unacknowledged packets of a
// channel, and the client acknowledges a window per round trip, which frees the window for the
// packets that wait.
public sealed class EntryBurstTests
{
    // The unacknowledged packets per reliable channel that LiteNetLib has in flight.
    private const int SendWindow = 64;

    // How often LiteNetLib's update thread moves packets into the windows, and so the shortest
    // time in which a window can be acknowledged and filled again.
    private const double TransportUpdateMilliseconds = 15;

    // More entities than the queue limit holds: the limit is 1,024 queued packets, which two
    // packets per entity reach at 512 entities.
    private const int Crowd = 700;

    // The crowd, with every connection of it a peer whose queue is modelled, on a line that keeps
    // up: it enters in bursts of at most the budget, the nearest first, and nobody is disconnected.
    [Fact]
    public void ACrowdBeyondTheQueueLimitEntersInBoundedBurstsNearestFirstAndNobodyIsDisconnected()
    {
        var manager = new SimulationInterestManager(InterestManagementConfig.Default);
        var run = RunCrowd(manager, roundTripMilliseconds: 0, everyoneIsAPeer: true);

        var limit = run.Limit;
        Assert.True(Crowd * 2 > limit, "The crowd must pass the limit in one burst.");
        Assert.Empty(run.Disconnected);
        run.AssertEveryEntityArrivedNearestFirst();
        Assert.All(run.Batches, batch => Assert.InRange(batch.Count, 1, SimulationInterestManager.MaximumEnteredPerRefresh));
        Assert.Equal((Crowd + 1 + SimulationInterestManager.MaximumEnteredPerRefresh - 1) / SimulationInterestManager.MaximumEnteredPerRefresh, run.Batches.Count);

        // A burst is 2 packets per entity, all of them in the queue when the supervisor looks, and
        // the little else that the tick sends comes on top; the queue was empty at the refresh.
        Assert.InRange(run.WorstQueued, 2 * SimulationInterestManager.MaximumEnteredPerRefresh, limit / 2);
        Assert.Equal(0, manager.DeferredByQueueCount);
        Assert.True(manager.DeferredByBurstCount > 0);
        Assert.Equal(0, run.LoopFailures);

        // The whole crowd is in the view within the 0.4 to 0.6 s after the join that the refreshes
        // take, and the client has all of it a moment later.
        Assert.InRange(run.EnteredSeconds, 0.3, 1.2);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A line that keeps up: the crowd of {Crowd + 1} was in the view after {run.EnteredSeconds:F1} s and delivered after {run.DeliveredSeconds:F1} s, in {run.Batches.Count} batches, with a worst queue of {run.WorstQueued}.");
    }

    // The same crowd around a client that acknowledges a window per round trip of 200 or 400 ms,
    // 320 or 160 packets a second: the crowd fills in as the client acknowledges, nobody is
    // disconnected, the queue never passes the limit, and every entity arrives nearest first.
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public void ASlowClientSeesTheCrowdFillInSlowlyInsteadOfBeingDisconnected(int roundTripMilliseconds)
    {
        var manager = new SimulationInterestManager(InterestManagementConfig.Default);
        var run = RunCrowd(manager, roundTripMilliseconds);

        Assert.Empty(run.Disconnected);
        Assert.InRange(run.WorstQueued, 1, run.Limit);
        run.AssertEveryEntityArrivedNearestFirst();

        // The queue held entities back, which the counter tells from a crowd that is only big.
        Assert.True(manager.DeferredByQueueCount > 0);
        Assert.True(run.Batches.Count > (Crowd + SimulationInterestManager.MaximumEnteredPerRefresh) / SimulationInterestManager.MaximumEnteredPerRefresh);

        // The line itself bounds the time: it delivers a window of 64 packets per round trip, 320
        // packets a second at 200 ms, and the crowd of 701 needs about 1,400 packets.
        var packetsPerSecond = SendWindow * 1000d / roundTripMilliseconds;
        var minimumSeconds = ((Crowd + 1) * 2d) / packetsPerSecond;
        Assert.True(run.DeliveredSeconds >= minimumSeconds * 0.9, $"Delivered in {run.DeliveredSeconds:F1} s, faster than the line allows.");
        Assert.True(run.DeliveredSeconds <= (minimumSeconds * 1.5) + 2, $"Delivered in {run.DeliveredSeconds:F1} s of {minimumSeconds:F1} s.");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Round trip {roundTripMilliseconds} ms: the crowd of {Crowd + 1} was in the view after {run.EnteredSeconds:F1} s and delivered after {run.DeliveredSeconds:F1} s, in {run.Batches.Count} batches, with a worst queue of {run.WorstQueued}.");
    }

    // The premise of the test above: the budget that follows the queue is what keeps the slow
    // client connected. With a fixed burst at every refresh the same client is disconnected.
    [Fact]
    public void AFixedBurstWouldHaveDisconnectedTheSlowClientIntoTheSameCrowdAgain()
    {
        var manager = new SimulationInterestManager(InterestManagementConfig.Default)
        {
            EntriesAllowedSource = _ => SimulationInterestManager.MaximumEnteredPerRefresh
        };

        var run = RunCrowd(manager, roundTripMilliseconds: 200);

        Assert.Equal([1], run.Disconnected);
    }

    // A client that acknowledges nothing for three seconds and then recovers: its queue stops
    // growing at the mark and it is not disconnected, no entity enters while it is full, and the
    // crowd fills in once the client acknowledges again.
    [Fact]
    public void AClientThatAcknowledgesNothingForAWhileGetsNoEntriesWhileItIsFullAndThenRecovers()
    {
        var manager = new SimulationInterestManager(InterestManagementConfig.Default);
        const int StallFrom = 12;
        const int StallTicks = 90;
        var run = RunCrowd(manager, roundTripMilliseconds: 100, stalled: tick => tick >= StallFrom && tick < StallFrom + StallTicks);

        Assert.Empty(run.Disconnected);
        Assert.InRange(run.WorstQueued, 1, run.Limit);
        run.AssertEveryEntityArrivedNearestFirst();

        // The queue filled to the mark and entries stopped there: the view of the stalled client
        // did not grow in the last two seconds of the stall.
        var mark = ReliableQueueEntryBudget.HighWaterOf(run.Limit);
        Assert.InRange(run.WorstQueued, mark - (2 * SimulationInterestManager.MaximumEnteredPerRefresh), mark + (2 * SimulationInterestManager.MaximumEnteredPerRefresh));
        Assert.Equal(run.VisibleAtTick[StallFrom + StallTicks - 60], run.VisibleAtTick[StallFrom + StallTicks - 1]);
        Assert.True(run.VisibleAtTick[StallFrom + StallTicks - 1] < Crowd + 1);
        Assert.True(manager.DeferredByQueueCount > 0);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Stalled for {StallTicks} ticks from tick {StallFrom}: the view held {run.VisibleAtTick[StallFrom + StallTicks - 1]} entities, the worst queue was {run.WorstQueued}, and the crowd was delivered after {run.DeliveredSeconds:F1} s.");
    }

    // Plays the crowd tick by tick: one observer in the middle with the crowd around it at
    // distances that are all different and not in the order of the entity ids.
    private static CrowdResult RunCrowd(
        SimulationInterestManager manager,
        int roundTripMilliseconds,
        bool everyoneIsAPeer = false,
        Func<int, bool>? stalled = null)
    {
        using var fixture = new CombatTickFixture(
            RealtimeCombatTests.TestRules(),
            new ItemCatalogRuntimeDocument(),
            snapshots: true,
            interestManager: manager);
        var limit = fixture.Config.Admission.MaxReliableQueuedPackets;
        var tickMilliseconds = 1000d / fixture.TickRateHz;
        var observer = fixture.Join(1, "Observer");
        var distances = new Dictionary<ulong, float> { [observer.EntityId] = 0f };
        var peers = new List<CombatTickPlayer> { observer };
        Place(observer, 0f, 0f);
        for (var index = 0; index < Crowd; index++)
        {
            var member = fixture.Join(2 + index, $"Crowd {index}");
            var distance = 1f + (((index * 37) % Crowd) * 0.15f);
            Place(member, distance, 0f);
            distances[member.EntityId] = distance;
            if (everyoneIsAPeer)
            {
                peers.Add(member);
            }
            else
            {
                // The rest of the crowd stands as lingering bodies, which need no peer.
                fixture.Leave(member);
            }
        }

        var models = peers.Select(peer => new PeerQueueModel(peer, roundTripMilliseconds, tickMilliseconds)).ToArray();
        var players = new List<PlayerSimulationEntity>();
        fixture.Entities.CopyPlayersTo(players);
        fixture.Core.Presence.SynchronizeInterestIndex(players);

        // The join of the observer: its view and the baseline of what is in it, then the first
        // supervision.
        fixture.ShowWorldTo(observer);
        var worstQueued = 0;
        var batches = new List<List<ulong>> { SpawnedSince(observer, 0) };
        var visibleAtTick = new List<int> { fixture.Core.Presence.GetVisibleOrdered(observer.Context.PeerId).Count };
        worstQueued = Math.Max(worstQueued, Supervise(fixture, models));
        var deliveredTick = -1;
        var maximumTicks = 30 * fixture.TickRateHz;
        for (var tick = 1; tick <= maximumTicks && fixture.Directory.Disconnected.Count == 0; tick++)
        {
            foreach (var model in models)
            {
                model.Acknowledges = stalled is null || !stalled(tick);
            }

            fixture.RunTick();
            worstQueued = Math.Max(worstQueued, Supervise(fixture, models));
            var batch = SpawnedSince(observer, batches.Sum(known => known.Count));
            if (batch.Count > 0)
            {
                batches.Add(batch);
            }

            var visible = fixture.Core.Presence.GetVisibleOrdered(observer.Context.PeerId).Count;
            visibleAtTick.Add(visible);
            if (visible == Crowd + 1 && models[0].IsIdle)
            {
                deliveredTick = tick;
                break;
            }
        }

        return new CrowdResult(
            observer,
            distances,
            limit,
            batches,
            visibleAtTick,
            worstQueued,
            deliveredTick,
            deliveredTick * tickMilliseconds / 1000d,
            visibleAtTick.FindIndex(visible => visible == Crowd + 1) * tickMilliseconds / 1000d,
            [.. fixture.Directory.Disconnected.Select(disconnect => disconnect.PeerId)],
            fixture.LoopGuard.TotalFailures,
            fixture.Core.Presence.GetVisibleOrdered(observer.Context.PeerId).Count,
            observer.Received<RealtimeCombatantState>(RealtimeMessageType.CombatantState, RealtimeProtocol.TryDecodeCombatantState)
                .Select(state => state.EntityId)
                .Distinct()
                .Order()
                .ToArray());
    }

    // What the supervisor sees right after a tick: every modelled peer's queue before the
    // transport moved anything, and then the transport and the acknowledgements. Returns the
    // largest queue it saw.
    private static int Supervise(CombatTickFixture fixture, PeerQueueModel[] models)
    {
        var worst = 0;
        foreach (var model in models)
        {
            worst = Math.Max(worst, model.AfterTick());
        }

        fixture.EnforcePeerState();
        foreach (var model in models)
        {
            model.AfterSupervision();
        }

        return worst;
    }

    // The entities of the spawns that the player's peer got since its first skip, in the order they
    // arrived.
    private static List<ulong> SpawnedSince(CombatTickPlayer player, int skip)
    {
        return player.Received<RealtimeEntitySpawn>(RealtimeMessageType.EntitySpawn, RealtimeProtocol.TryDecodeEntitySpawn)
            .Skip(skip)
            .Select(spawn => spawn.EntityId)
            .ToList();
    }

    private static void Place(CombatTickPlayer player, float x, float z)
    {
        player.Entity.Movement.Teleport(new PlayerMovementState(x, 0f, z, 0f, 0f, 0f, 0f, true));
    }

    // The reliable channels of one peer as the worker sees them. What a tick sends joins the
    // outgoing queue of its channel; after the supervisor looked, the transport moves packets into
    // the window of the channel, the client acknowledges a window per round trip, and the transport
    // moves more into the room that frees. The queue that the next refresh reads is what waits
    // after that.
    private sealed class PeerQueueModel(CombatTickPlayer player, double roundTripMilliseconds, double tickMilliseconds)
    {
        private readonly Dictionary<byte, int> waiting = [];
        private readonly Dictionary<byte, double> inFlight = [];
        private int counted;

        // False while the client acknowledges nothing.
        public bool Acknowledges { get; set; } = true;

        public bool IsIdle => waiting.Values.Sum() == 0 && inFlight.Values.Sum() < 1d;

        // The packets that the tick sent join the queues, which the supervisor looks at now.
        public int AfterTick()
        {
            var sent = player.Peer.Sent;
            for (; counted < sent.Count; counted++)
            {
                if (sent[counted].Delivery == DeliveryMethod.ReliableOrdered)
                {
                    waiting[sent[counted].Channel] = waiting.GetValueOrDefault(sent[counted].Channel) + 1;
                }
            }

            Publish();
            return waiting.Values.Sum();
        }

        public void AfterSupervision()
        {
            MoveIntoTheWindows();
            if (Acknowledges)
            {
                var windowsPerTick = tickMilliseconds / Math.Max(roundTripMilliseconds, TransportUpdateMilliseconds);
                foreach (var channel in inFlight.Keys.ToArray())
                {
                    inFlight[channel] = Math.Max(0d, inFlight[channel] - (SendWindow * windowsPerTick));
                }
            }

            MoveIntoTheWindows();
            Publish();
        }

        private void MoveIntoTheWindows()
        {
            foreach (var channel in waiting.Keys.ToArray())
            {
                var room = (int)Math.Floor(SendWindow - inFlight.GetValueOrDefault(channel));
                var moved = Math.Max(0, Math.Min(waiting[channel], room));
                waiting[channel] -= moved;
                inFlight[channel] = inFlight.GetValueOrDefault(channel) + moved;
            }
        }

        private void Publish()
        {
            foreach (var (channel, queued) in waiting)
            {
                player.Peer.QueuedReliablePackets[channel] = queued;
            }
        }
    }

    private sealed record CrowdResult(
        CombatTickPlayer Observer,
        Dictionary<ulong, float> Distances,
        int Limit,
        List<List<ulong>> Batches,
        List<int> VisibleAtTick,
        int WorstQueued,
        int DeliveredTick,
        double DeliveredSeconds,
        double EnteredSeconds,
        IReadOnlyList<int> Disconnected,
        long LoopFailures,
        int FinalVisible,
        ulong[] CombatantStateEntities)
    {
        // Every entity arrived once with its combatant state, and the batches that came together
        // are nearest first inside, and each is nearer than every later one.
        public void AssertEveryEntityArrivedNearestFirst()
        {
            Assert.True(DeliveredTick > 0, "The crowd was not delivered within 30 seconds.");
            var arrived = Batches.SelectMany(batch => batch).ToArray();
            Assert.Equal(Crowd + 1, arrived.Length);
            Assert.Equal(Crowd + 1, arrived.Distinct().Count());
            Assert.Contains(Observer.EntityId, Batches[0]);
            Assert.Equal(Observer.EntityId, Batches[0][0]);
            foreach (var batch in Batches)
            {
                Assert.Equal(batch.OrderBy(entityId => Distances[entityId]), batch);
            }

            for (var batch = 1; batch < Batches.Count; batch++)
            {
                Assert.True(
                    Batches[batch - 1].Max(entityId => Distances[entityId])
                        <= Batches[batch].Min(entityId => Distances[entityId]),
                    $"Batch {batch} holds an entity nearer than one of the batch before it.");
            }

            Assert.Equal(arrived.Order(), CombatantStateEntities);
            Assert.Equal(Crowd + 1, FinalVisible);
        }
    }
}
