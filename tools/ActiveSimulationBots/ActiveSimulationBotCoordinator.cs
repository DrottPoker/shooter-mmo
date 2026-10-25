using System.Diagnostics;
using Mmorpg.Server.Common.Timing;
using Mmorpg.Tools.SimulationBotClient;
using HeadlessSimulationBotClient = Mmorpg.Tools.SimulationBotClient.SimulationBotClient;

namespace Mmorpg.Tools.ActiveSimulationBots;

public sealed class ActiveSimulationBotCoordinator(
    ActiveSimulationBotOptions options,
    ActiveSimulationBotAuthorityClient authorityClient)
{
    // Bots poll and send due inputs this often, about the frame time of a 300 FPS client.
    // Task.Delay waited a whole 15.6 ms Windows timer tick, so bots sent inputs in bursts.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(3);

    // The build copies the compiled World collision below the tool's output.
    private const string WorldDataPath = "WorldData/Worlds";

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
    private readonly Dictionary<string, ActiveSimulationBotNavigation?> navigations =
        new(StringComparer.Ordinal);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var pollDelay = new PreciseDelay("ActiveSimulationBots poll loop");
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
                nextSpawn = StartDueTicketRequests(now, nextSpawn, cancellationToken);

                if (now >= nextStatus)
                {
                    WriteStatus();
                    nextStatus = AddDuration(now, options.StatusInterval);
                }

                try
                {
                    await pollDelay.DelayAsync(PollInterval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            await ShutdownAsync(pollDelay);
            foreach (var navigation in navigations.Values)
            {
                navigation?.Dispose();
            }
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
                new ActiveSimulationBotInputSource(
                    options,
                    slot.Index,
                    NavigationFor(ticket.WorldId)));
            slot.Client = client;
            slot.JoinObserved = false;
            slot.LeaveRequested = false;
            client.Start();
        }
    }

    // Loads a World's collision for planned walks once; bots of every World keep theirs until
    // the tool stops. Bots walk straight lines when it cannot be loaded.
    private ActiveSimulationBotNavigation? NavigationFor(string worldId)
    {
        if (navigations.TryGetValue(worldId, out var loaded))
        {
            return loaded;
        }

        ActiveSimulationBotNavigation? navigation = null;
        try
        {
            navigation = ActiveSimulationBotNavigation.Load(
                WorldDataPath,
                worldId,
                Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
            Console.WriteLine(
                $"Bots plan their walks on the collision of World {worldId}, revision {navigation.CollisionWorld.Revision}.");
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or ArgumentException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            Console.WriteLine(
                $"Bots walk straight lines, because the collision of World {worldId} could not be loaded: {exception.Message}");
        }

        navigations.Add(worldId, navigation);
        return navigation;
    }

    private void ProcessClientTransitions(long nowTimestamp)
    {
        foreach (var slot in slots.Where(slot => slot.Client is not null))
        {
            var client = slot.Client!;
            if (client.State == SimulationBotClientState.Joined && !slot.JoinObserved)
            {
                ObserveJoin(slot, client.Capture(), nowTimestamp);
            }

            if (client.State is not (SimulationBotClientState.Completed
                or SimulationBotClientState.Failed))
            {
                continue;
            }

            var snapshot = client.Capture();
            // A bot can join and end its session in the same poll.
            if (!slot.JoinObserved && snapshot.JoinLatencyMs is not null)
            {
                ObserveJoin(slot, snapshot, nowTimestamp);
            }

            if (snapshot.State == SimulationBotClientState.Completed)
            {
                gracefulLogouts++;
                // Only a clean session resets the backoff, so a bot that fails right after
                // every join backs off instead of reconnecting every few seconds.
                slot.ConsecutiveFailures = 0;
                Console.WriteLine($"{snapshot.CharacterName} logged out cleanly.");
                FinishClient(
                    slot,
                    AddDuration(
                        nowTimestamp,
                        RandomDuration(
                            options.MinimumOfflineDuration,
                            options.MaximumOfflineDuration)));
            }
            else
            {
                botSessionFailures++;
                Console.WriteLine(
                    $"{snapshot.CharacterName} failed: {snapshot.FailureCode}: {snapshot.FailureMessage}");
                FinishClient(slot, nowTimestamp);
                ScheduleRetry(slot, nowTimestamp);
            }
        }
    }

    private void ObserveJoin(
        ActiveBotSlot slot,
        SimulationBotClientSnapshot snapshot,
        long nowTimestamp)
    {
        slot.JoinObserved = true;
        slot.OnlineUntilTimestamp = AddDuration(
            nowTimestamp,
            RandomDuration(
                options.MinimumOnlineDuration,
                options.MaximumOnlineDuration));
        successfulLogins++;
        Console.WriteLine($"{snapshot.CharacterName} joined the shard.");
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

    // Starts every ticket request that is due at the startup rate, so rates above one per poll
    // hold. While no request can start, no backlog builds up, so a ramp never bursts.
    private long StartDueTicketRequests(
        long nowTimestamp,
        long nextSpawnTimestamp,
        CancellationToken cancellationToken)
    {
        var spawnInterval = AddDuration(0, TimeSpan.FromSeconds(1d / options.StartupBotsPerSecond));
        while (nowTimestamp >= nextSpawnTimestamp)
        {
            if (!ActiveSimulationBotPopulationPolicy.CanStartConnection(
                    CountDesiredOnlineBots(),
                    targetPopulation,
                    CountConnectionOccupancy(),
                    options.MaximumActiveBots)
                || !StartOneTicketRequest(nowTimestamp, cancellationToken))
            {
                return nowTimestamp;
            }

            nextSpawnTimestamp += spawnInterval;
        }

        return nextSpawnTimestamp;
    }

    private bool StartOneTicketRequest(
        long nowTimestamp,
        CancellationToken cancellationToken)
    {
        var slot = slots.FirstOrDefault(candidate =>
            candidate.Client is null
            && candidate.TicketRequest is null
            && candidate.EligibleAtTimestamp <= nowTimestamp);
        if (slot is null)
        {
            return false;
        }

        slot.TicketRequest = authorityClient.IssueTicketAsync(
            slot.InstanceId,
            slot.Index,
            cancellationToken);
        return true;
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
        slot.EligibleAtTimestamp = AddDuration(
            nowTimestamp,
            ActiveSimulationBotPopulationPolicy.GetRetryDelay(
                slot.ConsecutiveFailures,
                options.MinimumRetryDelay,
                options.MaximumRetryDelay,
                random.NextDouble()));
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

    private async Task ShutdownAsync(PreciseDelay pollDelay)
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
            await pollDelay.DelayAsync(PollInterval, CancellationToken.None);
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
