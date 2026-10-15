using LiteNetLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mmorpg.GameProtocol;
using Mmorpg.SimulationWorker.Config;
using Mmorpg.SimulationWorker.Entities;
using Mmorpg.SimulationWorker.Realtime;
using Mmorpg.SimulationWorker.Sessions;

namespace Mmorpg.SimulationWorker.Tests.Realtime;

public sealed class RealtimeCompletionProcessorTests
{
    [Fact]
    public void ExhaustedBudgetStillProcessesOneCompletionPerPass()
    {
        var fixture = new ProcessorFixture();
        var handler = new RecordingCompletionHandler();
        var (context, _) = fixture.AddPeer(1);
        for (var index = 0; index < 3; index++)
        {
            fixture.Completions.Post(handler, CreateLeave(context));
        }

        fixture.Processor.ProcessCompleted(TimeSpan.Zero);

        Assert.Single(handler.Completed);
        Assert.Equal(2, fixture.Completions.Count);

        fixture.Processor.ProcessCompleted(TimeSpan.FromMinutes(1));

        Assert.Equal(3, handler.Completed.Count);
        Assert.Equal(0, fixture.Completions.Count);
    }

    [Fact]
    public void CompletionsRunInOneFifoAcrossHandlers()
    {
        var fixture = new ProcessorFixture();
        var order = new List<string>();
        var first = new RecordingCompletionHandler("first", order);
        var second = new RecordingCompletionHandler("second", order);
        var (context, _) = fixture.AddPeer(1);

        fixture.Completions.Post(first, CreateLeave(context));
        fixture.Completions.Post(second, CreateLeave(context));
        fixture.Completions.Post(first, CreateLeave(context));
        fixture.Processor.ProcessCompleted(TimeSpan.FromMinutes(1));

        Assert.Equal(["first", "second", "first"], order);
    }

    [Fact]
    public void FailedCompletionLogsNotifiesTheHandlerAndThenDisconnectsItsPeer()
    {
        var logger = new CapturingLogger<RealtimeCompletionProcessor>();
        var fixture = new ProcessorFixture(logger);
        var (failedContext, failedPeer) = fixture.AddPeer(1);
        var (otherContext, otherPeer) = fixture.AddPeer(2);
        var order = new List<string>();
        var failing = new RecordingCompletionHandler("failing", order)
        {
            ThrowFor = failedContext,
            OnFailed = () => Assert.Empty(failedPeer.Sent)
        };

        fixture.Completions.Post(failing, CreateLeave(failedContext));
        fixture.Completions.Post(failing, CreateLeave(otherContext));
        fixture.Processor.ProcessCompleted(TimeSpan.FromMinutes(1));

        Assert.Equal(["failing:failed", "failing"], order);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(
            "[SIMULATION] Could not process a completed {OperationType} for UDP peer {PeerId}.",
            entry.Template);
        Assert.Contains("LeaveCompleted", entry.Message, StringComparison.Ordinal);
        var sent = Assert.Single(failedPeer.Sent);
        Assert.Equal(DeliveryMethod.ReliableOrdered, sent.Delivery);
        Assert.True(RealtimeProtocol.TryDecodeServerDisconnect(sent.Data, out var reason, out _));
        Assert.Equal("internal_error", reason.Code);
        Assert.NotNull(failedContext.DisconnectAfterUtc);
        Assert.Empty(otherPeer.Sent);
        Assert.Null(otherContext.DisconnectAfterUtc);
    }

    [Fact]
    public void FailedCompletionOfAReplacedPeerIdIsNotSentToTheNewPeer()
    {
        var fixture = new ProcessorFixture();
        var (staleContext, _) = fixture.AddPeer(1);
        var (_, currentPeer) = fixture.AddPeer(1);
        var failing = new RecordingCompletionHandler { ThrowFor = staleContext };

        fixture.Completions.Post(failing, CreateLeave(staleContext));
        fixture.Processor.ProcessCompleted(TimeSpan.FromMinutes(1));

        Assert.True(failing.FailureNotified);
        Assert.Empty(currentPeer.Sent);
    }

    private static LeaveCompleted CreateLeave(RealtimePeerContext context)
    {
        return new LeaveCompleted(
            context,
            RealtimeTestSessions.Create(),
            SimulationSessionReleaseResult.Success());
    }

    private sealed class ProcessorFixture
    {
        private readonly RecordingPeerDirectory directory = new();

        public ProcessorFixture(ILogger? logger = null)
        {
            Registry = new RealtimePeerRegistry(
                directory,
                new ConnectionEntityBindingRegistry(),
                new SimulationEntityRegistry(),
                Metrics);
            Processor = new RealtimeCompletionProcessor(
                Completions,
                Registry,
                new RealtimePeerSender(Metrics, NullLogger.Instance),
                logger ?? NullLogger.Instance);
        }

        public RealtimeNetworkMetrics Metrics { get; } = new();

        public RealtimeCompletionQueue Completions { get; } = new();

        public RealtimePeerRegistry Registry { get; }

        public RealtimeCompletionProcessor Processor { get; }

        public (RealtimePeerContext Context, RecordingPeerConnection Peer) AddPeer(int peerId)
        {
            var peer = new RecordingPeerConnection(peerId);
            directory.Peers[peerId] = peer;
            var context = new RealtimePeerContext(peerId, DateTime.UtcNow, UdpQuotaConfig.Default);
            Registry.Register(context);
            return (context, peer);
        }
    }

    private sealed class RecordingCompletionHandler(
        string name = "handler",
        List<string>? order = null) : IRealtimeCompletionHandler
    {
        public List<RealtimeOperationResult> Completed { get; } = [];

        public RealtimePeerContext? ThrowFor { get; init; }

        public Action? OnFailed { get; init; }

        public bool FailureNotified { get; private set; }

        public void Complete(RealtimeOperationResult completion)
        {
            if (ReferenceEquals(completion.Context, ThrowFor))
            {
                throw new InvalidOperationException("Completion failure.");
            }

            Completed.Add(completion);
            order?.Add(name);
        }

        public void OnCompletionFailed(RealtimeOperationResult completion)
        {
            OnFailed?.Invoke();
            FailureNotified = true;
            order?.Add($"{name}:failed");
        }
    }
}
