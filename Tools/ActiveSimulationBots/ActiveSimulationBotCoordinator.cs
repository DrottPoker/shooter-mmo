using System.Diagnostics;
using ShooterMmo.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = ShooterMmo.Tools.SimulationBotClient.SimulationBotClient;

namespace ShooterMmo.Tools.ActiveSimulationBots;

public sealed class ActiveSimulationBotCoordinator(
    ActiveSimulationBotOptions options,
    ActiveSimulationBotAuthorityClient authorityClient)
{
    private readonly List<ActiveBotSlot> slots = Enumerable
        .Range(1, options.MaximumActiveBots)
        .Select(index => new ActiveBotSlot(index, Guid.NewGuid()))
        .ToList();
    private readonly Random random = new(options.Seed);
    private int targetPopulation = options.MinimumActiveBots;
    private long successfulLogins;
    private long gracefulLogouts;
    private long botSessionFailures;
    private long ticketRejections;
    private long lifetimePacketsSent;
    private long lifetimePacketsReceived;
    private long lifetimeBytesSent;
    private long lifetimeBytesReceived;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var nextPopulationChange = AddDuration(now, options.PopulationChangeInterval);
        var nextStatus = now;
        var nextSpawn = now;

        Console.WriteLine(
            $"ActiveSimulationBots targeting shard {options.ShardId} through {options.AuthorityUrl}.");
        Console.WriteLine(
            $"Population range {options.MinimumActiveBots} to {options.MaximumActiveBots}; initial target {targetPopulation}.");
        Console.WriteLine("Press Ctrl+C to request graceful logout for all joined bots.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                now = Stopwatch.GetTimestamp();
                PollClients(now);
                await ProcessCompletedTicketRequestsAsync(now, cancellationToken);
                ProcessClientTransitions(now);
                RequestExpiredOnlineSessions(now);

                if (now >= nextPopulationChange)
                {
                    targetPopulation = random.Next(
                        options.MinimumActiveBots,
                        options.MaximumActiveBots + 1);
                    nextPopulationChange = AddDuration(now, options.PopulationChangeInterval);
                    Console.WriteLine($"Population target changed to {targetPopulation} bots.");
                }

                ReducePopulationToTarget(now);
                if (ActiveSimulationBotPopulationPolicy.CanStartConnection(
                        CountDesiredOnlineBots(),
                        targetPopulation,
                        CountConnectionOccupancy(),
                        options.MaximumActiveBots)
                    && now >= nextSpawn)
                {
                    StartOneTicketRequest(now, cancellationToken);
                    nextSpawn = AddDuration(
                        now,
                        TimeSpan.FromSeconds(1d / options.StartupBotsPerSecond));
                }

                if (now >= nextStatus)
                {
                    WriteStatus();
                    nextStatus = AddDuration(now, options.StatusInterval);
                }

                try
                {
                    await Task.Delay(5, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private void PollClients(long nowTimestamp)
    {
        foreach (var slot in slots)
        {
            slot.Client?.Poll(nowTimestamp);
        }
    }

    private async Task ProcessCompletedTicketRequestsAsync(
        long nowTimestamp,
        CancellationToken cancellationToken)
    {
        foreach (var slot in slots.Where(slot => slot.TicketRequest?.IsCompleted == true))
        {
            ActiveSimulationBotTicketResult result;
            try
            {
                result = await slot.TicketRequest!;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                slot.TicketRequest = null;
                continue;
            }

            slot.TicketRequest = null;
            if (!result.Succeeded)
            {
                ticketRejections++;
                ScheduleRetry(slot, nowTimestamp);
                Console.WriteLine(
                    $"Bot {slot.Index} ticket rejected: {result.ErrorCode}: {result.ErrorMessage}");
                continue;
            }

            var ticket = result.Ticket!;
            var client = new HeadlessSimulationBotClient(
                new SimulationBotConnectionOptions(
                    ticket.Endpoint.Host,
                    ticket.Endpoint.UdpPort,
                    options.JoinAndLeaveTimeout),
                new SimulationBotIdentity(
                    slot.Index,
                    ticket.AccountId,
                    ticket.CharacterId,
                    ticket.CharacterName,
                    ticket.JoinTicket,
                    ticket.ShardId,
                    ticket.WorldId),
                new ActiveSimulationBotInputSource(options, slot.Index));
            slot.Client = client;
            slot.JoinObserved = false;
            slot.LeaveRequested = false;
            client.Start();
        }
    }

    private void ProcessClientTransitions(long nowTimestamp)
    {
        foreach (var slot in slots.Where(slot => slot.Client is not null))
        {
            var client = slot.Client!;
            if (client.State == SimulationBotClientState.Joined && !slot.JoinObserved)
            {
                slot.JoinObserved = true;
                slot.ConsecutiveFailures = 0;
                slot.OnlineUntilTimestamp = AddDuration(
                    nowTimestamp,
                    RandomDuration(
                        options.MinimumOnlineDuration,
                        options.MaximumOnlineDuration));
                successfulLogins++;
                Console.WriteLine($"{client.Capture().CharacterName} joined the shard.");
            }

            if (client.State == SimulationBotClientState.Completed)
            {
                gracefulLogouts++;
                Console.WriteLine($"{client.Capture().CharacterName} logged out cleanly.");
                FinishClient(
                    slot,
                    AddDuration(
                        nowTimestamp,
                        RandomDuration(
                            options.MinimumOfflineDuration,
                            options.MaximumOfflineDuration)));
            }
            else if (client.State == SimulationBotClientState.Failed)
            {
                var snapshot = client.Capture();
                botSessionFailures++;
                Console.WriteLine(
                    $"{snapshot.CharacterName} failed: {snapshot.FailureCode}: {snapshot.FailureMessage}");
                FinishClient(slot, nowTimestamp);
                ScheduleRetry(slot, nowTimestamp);
            }
        }
    }

    private void RequestExpiredOnlineSessions(long nowTimestamp)
    {
        var expiredCount = slots.Count(slot =>
            slot.Client?.State == SimulationBotClientState.Joined
            && !slot.LeaveRequested
            && slot.OnlineUntilTimestamp <= nowTimestamp);
        var availableLeaves = ActiveSimulationBotPopulationPolicy.GetAllowedIntentionalLeaves(
            CountJoinedBots(),
            options.MinimumActiveBots,
            expiredCount);
        foreach (var slot in slots
                     .Where(slot =>
                         slot.Client?.State == SimulationBotClientState.Joined
                         && !slot.LeaveRequested
                         && slot.OnlineUntilTimestamp <= nowTimestamp)
                     .OrderBy(slot => slot.OnlineUntilTimestamp)
                     .Take(availableLeaves))
        {
            RequestLeave(slot, nowTimestamp);
        }
    }

    private void ReducePopulationToTarget(long nowTimestamp)
    {
        var excess = CountDesiredOnlineBots() - targetPopulation;
        if (excess <= 0)
        {
            return;
        }

        var availableLeaves = ActiveSimulationBotPopulationPolicy.GetAllowedIntentionalLeaves(
            CountJoinedBots(),
            options.MinimumActiveBots,
            excess);
        foreach (var slot in slots
                     .Where(slot =>
                         slot.Client?.State == SimulationBotClientState.Joined
                         && !slot.LeaveRequested)
                     .OrderBy(slot => slot.OnlineUntilTimestamp)
                     .Take(availableLeaves))
        {
            RequestLeave(slot, nowTimestamp);
        }
    }

    private void StartOneTicketRequest(
        long nowTimestamp,
        CancellationToken cancellationToken)
    {
        var slot = slots.FirstOrDefault(candidate =>
            candidate.Client is null
            && candidate.TicketRequest is null
            && candidate.EligibleAtTimestamp <= nowTimestamp);
        if (slot is null)
        {
            return;
        }

        slot.TicketRequest = authorityClient.IssueTicketAsync(
            slot.InstanceId,
            slot.Index,
            cancellationToken);
    }

    private void RequestLeave(ActiveBotSlot slot, long nowTimestamp)
    {
        if (slot.Client?.State != SimulationBotClientState.Joined)
        {
            return;
        }

        slot.LeaveRequested = true;
        slot.Client.BeginLeave(nowTimestamp);
    }

    private void FinishClient(ActiveBotSlot slot, long eligibleAtTimestamp)
    {
        if (slot.Client is not null)
        {
            var snapshot = slot.Client.Capture();
            lifetimePacketsSent += snapshot.PacketsSent;
            lifetimePacketsReceived += snapshot.PacketsReceived;
            lifetimeBytesSent += snapshot.BytesSent;
            lifetimeBytesReceived += snapshot.BytesReceived;
            slot.Client.Dispose();
        }

        slot.Client = null;
        slot.JoinObserved = false;
        slot.LeaveRequested = false;
        slot.OnlineUntilTimestamp = 0;
        slot.EligibleAtTimestamp = eligibleAtTimestamp;
    }

    private void ScheduleRetry(ActiveBotSlot slot, long nowTimestamp)
    {
        slot.ConsecutiveFailures = Math.Min(slot.ConsecutiveFailures + 1, 10);
        var exponentialSeconds = options.MinimumRetryDelay.TotalSeconds
            * Math.Pow(2d, slot.ConsecutiveFailures - 1);
        var cappedSeconds = Math.Min(
            options.MaximumRetryDelay.TotalSeconds,
            exponentialSeconds);
        var jitteredSeconds = cappedSeconds * (0.75d + (random.NextDouble() * 0.5d));
        slot.EligibleAtTimestamp = AddDuration(
            nowTimestamp,
            TimeSpan.FromSeconds(jitteredSeconds));
    }

    private int CountDesiredOnlineBots()
    {
        return slots.Count(slot =>
            slot.TicketRequest is not null
            || slot.Client?.State is SimulationBotClientState.Connecting
                or SimulationBotClientState.Joining
                or SimulationBotClientState.Joined);
    }

    private int CountConnectionOccupancy()
    {
        return slots.Count(slot =>
            slot.TicketRequest is not null
            || slot.Client?.State is SimulationBotClientState.Connecting
                or SimulationBotClientState.Joining
                or SimulationBotClientState.Joined
                or SimulationBotClientState.Leaving);
    }

    private int CountJoinedBots()
    {
        return slots.Count(slot => slot.Client?.State == SimulationBotClientState.Joined);
    }

    private void WriteStatus()
    {
        var snapshots = slots
            .Where(slot => slot.Client is not null)
            .Select(slot => slot.Client!.Capture())
            .ToArray();
        var joined = snapshots.Count(snapshot => snapshot.State == SimulationBotClientState.Joined);
        var joining = snapshots.Count(snapshot => snapshot.State is
            SimulationBotClientState.Connecting or SimulationBotClientState.Joining);
        var leaving = snapshots.Count(snapshot => snapshot.State == SimulationBotClientState.Leaving);
        var pendingTickets = slots.Count(slot => slot.TicketRequest is not null);
        var packetsSent = lifetimePacketsSent + snapshots.Sum(snapshot => snapshot.PacketsSent);
        var packetsReceived = lifetimePacketsReceived + snapshots.Sum(snapshot => snapshot.PacketsReceived);
        var bytesSent = lifetimeBytesSent + snapshots.Sum(snapshot => snapshot.BytesSent);
        var bytesReceived = lifetimeBytesReceived + snapshots.Sum(snapshot => snapshot.BytesReceived);
        Console.WriteLine(
            $"Status: target {targetPopulation}, joined {joined}, joining {joining}, leaving {leaving}, ticket requests {pendingTickets}; lifetime logins {successfulLogins}, clean logouts {gracefulLogouts}, session failures {botSessionFailures}, ticket rejections {ticketRejections}; traffic sent {packetsSent} packets/{bytesSent} bytes, received {packetsReceived} packets/{bytesReceived} bytes.");
    }

    private async Task ShutdownAsync()
    {
        Console.WriteLine("Stopping ActiveSimulationBots and requesting graceful logout.");
        var now = Stopwatch.GetTimestamp();
        foreach (var slot in slots)
        {
            if (slot.Client?.State == SimulationBotClientState.Joined)
            {
                RequestLeave(slot, now);
            }
            else if (slot.Client?.State is SimulationBotClientState.Connecting
                or SimulationBotClientState.Joining)
            {
                FinishClient(slot, long.MaxValue);
            }
        }

        var deadline = AddDuration(now, options.ShutdownTimeout);
        while (Stopwatch.GetTimestamp() < deadline
               && slots.Any(slot => slot.Client?.State == SimulationBotClientState.Leaving))
        {
            now = Stopwatch.GetTimestamp();
            PollClients(now);
            ProcessClientTransitions(now);
            await Task.Delay(5);
        }

        foreach (var slot in slots)
        {
            if (slot.Client is not null)
            {
                FinishClient(slot, long.MaxValue);
            }
        }

        WriteStatus();
        Console.WriteLine("ActiveSimulationBots stopped.");
    }

    private TimeSpan RandomDuration(TimeSpan minimum, TimeSpan maximum)
    {
        return TimeSpan.FromSeconds(
            minimum.TotalSeconds
            + ((maximum.TotalSeconds - minimum.TotalSeconds) * random.NextDouble()));
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        return timestamp + (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed class ActiveBotSlot(int index, Guid instanceId)
    {
        public int Index { get; } = index;

        public Guid InstanceId { get; } = instanceId;

        public Task<ActiveSimulationBotTicketResult>? TicketRequest { get; set; }

        public HeadlessSimulationBotClient? Client { get; set; }

        public bool JoinObserved { get; set; }

        public bool LeaveRequested { get; set; }

        public long OnlineUntilTimestamp { get; set; }

        public long EligibleAtTimestamp { get; set; }

        public int ConsecutiveFailures { get; set; }
    }
}
