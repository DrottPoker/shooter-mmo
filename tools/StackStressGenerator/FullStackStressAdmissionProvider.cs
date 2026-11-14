using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Mmorpg.GameProtocol;

namespace Mmorpg.Tools.StackStressGenerator;

public sealed class FullStackStressAdmissionProvider : IStressAdmissionProvider
{
    private readonly StressGeneratorOptions options;
    private readonly HttpClient httpClient;
    private readonly FullStackStressMetrics metrics;
    private readonly FullStackStressClient client;
    private readonly StressPostgresSampler postgresSampler;
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<int, ActiveAccountSession> activeAccountSessions = [];
    private readonly ConcurrentDictionary<int, ProvisionedAccount> provisionedAccounts = [];
    private readonly ConcurrentDictionary<int, string> provisioningFailures = [];
    private readonly ConcurrentDictionary<int, AdmittedCharacter> admittedCharacters = [];
    private readonly object workerGate = new();
    private readonly string password;
    private readonly System.Net.IPEndPoint? redisEndpoint;
    private StressTableSnapshot? tablesAtSteadyStart;
    private StressTableSnapshot? tablesAtSteadyEnd;
    private StressRedisSnapshot? redisAtSteadyStart;
    private StressRedisSnapshot? redisAtSteadyEnd;
    private int steadyPlayers;
    private int churnLogouts;
    private int churnLogins;
    private StressProcessSampler? gameServiceProcessSampler;
    private StressWorkerRegistration? worker;
    private int registeredBots;
    private int fixtureAccounts;
    private int inventoryFixtureBots;
    private int provisionedBots;
    private int loggedInBots;
    private int admittedBots;
    private int loggedOutBots;
    private Guid? lootHotspotCorpseId;
    private StressFixtureDefinitions? fixtureDefinitions;

    public FullStackStressAdmissionProvider(StressGeneratorOptions options)
    {
        if (options.Mode != StressRunMode.FullStack)
        {
            throw new ArgumentException(
                "FullStackStressAdmissionProvider requires full-stack mode.",
                nameof(options));
        }

        this.options = options;
        metrics = new FullStackStressMetrics(options.Seed);
        httpClient = new HttpClient
        {
            BaseAddress = options.AuthorityUrl,
            Timeout = options.HttpTimeout
        };
        client = new FullStackStressClient(httpClient, metrics);
        postgresSampler = StressPostgresSampler.Create(
            options.PostgresConnectionString!,
            options.ConfirmedDisposableDatabase!,
            options.WorkerId,
            options.WorkerHeartbeatTimeoutSeconds,
            options.DatabasePoolSize);
        redisEndpoint = options.RedisEndpoint is null ? null : StressRedisSampler.ParseEndpoint(options.RedisEndpoint);
        concurrency = new SemaphoreSlim(options.HttpConcurrency, options.HttpConcurrency);
        password = $"StackStress_{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}!";
    }

    public async Task<StressRunTarget> WaitForReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp()
            + (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency);
        string? lastFailure = null;
        FullStackShardResponse? selectedShard = null;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await client.CheckReadyAsync(cancellationToken);
                var shards = await client.ListShardsAsync(cancellationToken);
                selectedShard = shards.SingleOrDefault(shard =>
                    string.Equals(shard.Id, options.ShardId, StringComparison.Ordinal));
                if (selectedShard is not null
                    && selectedShard.IsOnline
                    && selectedShard.Capacity > 0)
                {
                    break;
                }

                lastFailure = selectedShard is null
                    ? $"Shard '{options.ShardId}' was not returned by GameService."
                    : $"Shard '{options.ShardId}' is not online with available capacity.";
            }
            catch (FullStackApiException exception)
            {
                lastFailure = $"{exception.Code}: {exception.Message}";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        if (selectedShard is null
            || !selectedShard.IsOnline
            || selectedShard.Capacity <= 0)
        {
            throw new TimeoutException(
                $"The full stack did not become ready before the timeout. Last result: {lastFailure ?? "unknown"}");
        }

        if (!string.Equals(selectedShard.WorldId, options.WorldId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Shard '{selectedShard.Id}' uses World '{selectedShard.WorldId}', expected '{options.WorldId}'.");
        }

        await postgresSampler.InitializeAsync(cancellationToken);
        gameServiceProcessSampler = StressProcessSampler.TryAttach("Mmorpg.GameService");
        gameServiceProcessSampler?.Sample();

        Console.WriteLine(
            $"Full stack is ready for shard {selectedShard.Id}, World {selectedShard.WorldId}, capacity {selectedShard.Capacity}, and disposable database {options.ConfirmedDisposableDatabase}.");
        return new StressRunTarget(
            selectedShard.Id,
            selectedShard.WorldId,
            selectedShard.Capacity,
            null);
    }

    public async Task<StressAdmissionResult> AdmitAsync(
        int botIndex,
        CancellationToken cancellationToken)
    {
        if (options.Workload == StressWorkloadProfile.Playing)
        {
            return await AdmitProvisionedAsync(botIndex, cancellationToken);
        }

        await concurrency.WaitAsync(cancellationToken);
        string? activeToken = null;
        try
        {
            var username = $"stress_{options.RunId}_{botIndex}";
            var email = $"{username}@stack-stress.local";
            var characterName = $"Stack {options.RunId} {botIndex}";

            var registration = await client.RegisterAsync(
                email,
                username,
                password,
                cancellationToken);
            Interlocked.Increment(ref registeredBots);
            var character = await client.CreateCharacterAsync(
                registration.SessionToken,
                characterName,
                cancellationToken);
            Interlocked.Increment(ref provisionedBots);

            FullStackCharacterInventoryResponse? inventory = null;
            if (options.Workload != StressWorkloadProfile.Lifecycle)
            {
                var fixture = await client.SeedInventoryFixtureAsync(
                    registration.SessionToken,
                    character.Id,
                    StressGeneratorOptions.FormatWorkload(options.Workload),
                    options.RunId,
                    options.FixtureSecret!,
                    cancellationToken);
                inventory = fixture.Inventory;
                Volatile.Write(
                    ref fixtureDefinitions,
                    new StressFixtureDefinitions(fixture.InventoryDefinitionId, fixture.LootDefinitionId));
                Interlocked.Increment(ref inventoryFixtureBots);
            }

            await client.LogoutAsync(registration.SessionToken, cancellationToken);
            var login = await client.LoginAsync(username, password, cancellationToken);
            activeToken = login.SessionToken;
            Interlocked.Increment(ref loggedInBots);

            for (var requestIndex = 0;
                 requestIndex < options.SessionValidationRequestsPerBot;
                 requestIndex++)
            {
                await client.ValidateSessionAsync(login.SessionToken, cancellationToken);
            }

            var characters = await client.ListCharactersAsync(
                login.SessionToken,
                cancellationToken);
            if (!characters.Any(candidate => candidate.Id == character.Id))
            {
                throw new FullStackApiException(
                    "character_missing_after_login",
                    $"Character {character.Id} was not returned after login.",
                    0);
            }

            var shards = await client.ListShardsAsync(cancellationToken);
            var shard = shards.SingleOrDefault(candidate =>
                string.Equals(candidate.Id, options.ShardId, StringComparison.Ordinal));
            if (shard is null || !shard.IsOnline)
            {
                throw new FullStackApiException(
                    "stress_shard_unavailable",
                    $"Shard '{options.ShardId}' was not online during admission.",
                    0);
            }

            var join = await client.JoinShardAsync(
                login.SessionToken,
                options.ShardId,
                character.Id,
                cancellationToken);
            ValidateJoin(character, join);
            CaptureWorker(join);

            if (!activeAccountSessions.TryAdd(
                    botIndex,
                    new ActiveAccountSession(login.SessionToken, character.Id)))
            {
                throw new InvalidOperationException(
                    $"Bot index {botIndex} already owns a full-stack account session.");
            }

            activeToken = null;
            Interlocked.Increment(ref admittedBots);
            return StressAdmissionResult.Success(new StressBotAdmission(
                botIndex,
                login.AccountId,
                character.Id,
                character.Name,
                join.JoinTicket,
                join.ExpiresAt,
                join.Endpoint.Host,
                join.Endpoint.UdpPort,
                join.Shard.Id,
                join.Shard.WorldId,
                inventory,
                lootHotspotCorpseId,
                Volatile.Read(ref fixtureDefinitions)));
        }
        catch (FullStackApiException exception)
        {
            if (activeToken is not null)
            {
                await TryLogoutAfterFailureAsync(activeToken, cancellationToken);
            }

            return StressAdmissionResult.Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (activeToken is not null)
            {
                await TryLogoutAfterFailureAsync(activeToken, cancellationToken);
            }

            return StressAdmissionResult.Failure(
                "full_stack_admission_failed",
                exception.Message);
        }
        finally
        {
            concurrency.Release();
        }
    }

    public async Task CompleteAsync(
        IReadOnlyCollection<int> botIndexes,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            botIndexes,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.HttpConcurrency
            },
            async (botIndex, token) =>
            {
                if (!activeAccountSessions.TryRemove(botIndex, out var session))
                {
                    return;
                }

                try
                {
                    await client.LogoutAsync(session.SessionToken, token);
                    Interlocked.Increment(ref loggedOutBots);
                }
                catch (FullStackApiException exception)
                {
                    Console.WriteLine(
                        $"Bot {botIndex} account logout failed with {exception.Code}: {exception.Message}");
                }
            });

        // An account that was provisioned but never admitted logs out too.
        foreach (var leftover in provisionedAccounts.Values)
        {
            try
            {
                await client.LogoutAsync(leftover.SessionToken, cancellationToken);
            }
            catch (FullStackApiException exception)
            {
                Console.WriteLine($"A provisioned account logout failed with {exception.Code}: {exception.Message}");
            }
        }

        provisionedAccounts.Clear();
        await postgresSampler.VerifyAccountCountAsync(
            Volatile.Read(ref registeredBots) + Volatile.Read(ref fixtureAccounts),
            cancellationToken);
        await postgresSampler.VerifyGameplayInvariantsAsync(
            options.Workload,
            Volatile.Read(ref inventoryFixtureBots),
            lootHotspotCorpseId is not null,
            Volatile.Read(ref fixtureDefinitions),
            cancellationToken);
        await postgresSampler.SampleAsync(cancellationToken);
        gameServiceProcessSampler?.Sample();
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        gameServiceProcessSampler?.Sample();
        await postgresSampler.SampleAsync(cancellationToken);
    }

    public async Task<StressDriftSample?> SampleDriftAsync(int botsHoldingSession, int botsThatMayHoldSession, CancellationToken cancellationToken)
    {
        return await postgresSampler.SampleDriftAsync(botsHoldingSession, botsThatMayHoldSession, cancellationToken);
    }

    // The tables and Redis at one end of the steady interval. A count that fails or times out
    // fails the run with its reason.
    public async Task CaptureSteadyBoundaryAsync(bool start, int players, CancellationToken cancellationToken)
    {
        var tables = await postgresSampler.CountTablesAsync(cancellationToken);
        var redis = redisEndpoint is null ? null : await StressRedisSampler.ReadAsync(redisEndpoint, cancellationToken);
        if (start)
        {
            tablesAtSteadyStart = tables;
            redisAtSteadyStart = redis;
            steadyPlayers = players;
        }
        else
        {
            tablesAtSteadyEnd = tables;
            redisAtSteadyEnd = redis;
        }

        Console.WriteLine(
            $"Counted {tables.Tables.Count} tables with {tables.Tables.Sum(table => table.Rows)} rows and {tables.Tables.Sum(table => table.Bytes) / 1024d / 1024d:0.0} MiB in {tables.Milliseconds:0} ms at the {(start ? "start" : "end")} of the steady interval"
            + (redis is null ? "." : $"; Redis holds {redis.Keys} keys in {redis.UsedMemoryBytes / 1024d / 1024d:0.0} MiB."));
    }

    public void MarkSteadyEnd(long endTimestamp)
    {
        gameServiceProcessSampler?.MarkSteadyEnd(endTimestamp);
        postgresSampler.MarkSteadyEnd();
    }

    // The way back of a churning bot, as a player takes it: the account logout right after the
    // leave when the churn says so, the wait, the login after a logout, the character list, the
    // Shards, and the join ticket, which GameService places on the worker that holds the body
    // while it stands. For a body that is gone, the database's facts about it come along.
    public async Task<PlayingRejoinTicket> RejoinAsync(PlayingRejoinPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!admittedCharacters.TryGetValue(plan.BotIndex, out var admitted)
            || !activeAccountSessions.TryGetValue(plan.BotIndex, out var session))
        {
            throw new InvalidOperationException($"Bot {plan.BotIndex} has no account session to come back with.");
        }

        if (plan.Logout)
        {
            await client.LogoutAsync(session.SessionToken, cancellationToken);
            activeAccountSessions.TryRemove(plan.BotIndex, out _);
            Interlocked.Increment(ref churnLogouts);
        }

        var wait = (plan.RejoinAtTimestamp - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        if (wait > 0d)
        {
            await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        var token = session.SessionToken;
        if (plan.Logout)
        {
            var login = await client.LoginAsync(Username(plan.BotIndex), password, cancellationToken, paceRateLimit: true);
            token = login.SessionToken;
            activeAccountSessions[plan.BotIndex] = new ActiveAccountSession(token, admitted.Character.Id);
            Interlocked.Increment(ref churnLogins);
        }

        var characters = await client.ListCharactersAsync(token, cancellationToken);
        if (!characters.Any(candidate => candidate.Id == admitted.Character.Id))
        {
            throw new FullStackApiException(
                "character_missing_after_login",
                $"Character {admitted.Character.Id} was not returned on the way back.",
                0);
        }

        var shards = await client.ListShardsAsync(cancellationToken);
        if (!shards.Any(candidate =>
                string.Equals(candidate.Id, options.ShardId, StringComparison.Ordinal) && candidate.IsOnline))
        {
            throw new FullStackApiException(
                "stress_shard_unavailable",
                $"Shard '{options.ShardId}' was not online on the way back.",
                0);
        }

        var join = await client.JoinShardAsync(token, options.ShardId, admitted.Character.Id, cancellationToken);
        ValidateJoin(admitted.Character, join);
        CaptureWorker(join);
        var facts = join.IsReconnect ? null : await ReadRejoinFactsAsync(plan, cancellationToken);
        return new PlayingRejoinTicket(
            new StressBotAdmission(
                plan.BotIndex,
                admitted.AccountId,
                admitted.Character.Id,
                admitted.Character.Name,
                join.JoinTicket,
                join.ExpiresAt,
                join.Endpoint.Host,
                join.Endpoint.UdpPort,
                join.Shard.Id,
                join.Shard.WorldId),
            join.IsReconnect,
            started,
            facts);
    }

    public Task<PlayingRejoinFacts> ReadRejoinFactsAsync(PlayingRejoinPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!admittedCharacters.TryGetValue(plan.BotIndex, out var admitted)
            || !Guid.TryParse(plan.SimulationSessionId, out var session))
        {
            throw new InvalidOperationException($"Bot {plan.BotIndex} has no character or session to read the facts of.");
        }

        return postgresSampler.ReadRejoinFactsAsync(admitted.Character.Id, session, cancellationToken);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (options.Workload == StressWorkloadProfile.Playing)
        {
            await ProvisionPlayingAccountsAsync(cancellationToken);
            return;
        }

        if (options.Workload is not (
                StressWorkloadProfile.LootHotspot
                or StressWorkloadProfile.MixedGameplay))
        {
            return;
        }

        var username = $"stress_{options.RunId}_fixture";
        var sessionToken = string.Empty;
        try
        {
            var registration = await client.RegisterAsync(
                $"{username}@stack-stress.local",
                username,
                password,
                cancellationToken);
            sessionToken = registration.SessionToken;
            Interlocked.Increment(ref fixtureAccounts);
            var character = await client.CreateCharacterAsync(
                registration.SessionToken,
                $"Fixture {options.RunId}",
                cancellationToken);
            var join = await client.JoinShardAsync(
                registration.SessionToken,
                options.ShardId,
                character.Id,
                cancellationToken);
            ValidateJoin(character, join);
            CaptureWorker(join);

            var rampSteps = Math.Max(0, (int)Math.Ceiling(
                options.BotCount / (double)options.RampStep) - 1);
            // The hotspot is a player's corpse, which lasts five minutes: the fixture refuses a
            // run whose ramp and steady interval, with a minute for the bots' accounts, need more.
            var requestedLifetime = options.SteadyDuration
                + TimeSpan.FromTicks(options.RampInterval.Ticks * rampSteps)
                + TimeSpan.FromMinutes(1);
            var fixture = await client.CreateLootHotspotAsync(
                registration.SessionToken,
                new FullStackStackStressLootHotspotRequest(
                    options.RunId,
                    character.Id,
                    join.Shard.Id,
                    options.LootHotspotX,
                    options.LootHotspotY,
                    options.LootHotspotZ,
                    requestedLifetime.TotalSeconds),
                options.FixtureSecret!,
                cancellationToken);
            lootHotspotCorpseId = fixture.CorpseId;
            Console.WriteLine(
                $"Prepared loot hotspot corpse {fixture.CorpseId} with {fixture.LootStacks} stacks of {fixture.QuantityPerStack} items at ({options.LootHotspotX}, {options.LootHotspotY}, {options.LootHotspotZ}).");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                try
                {
                    await client.LogoutAsync(sessionToken, cancellationToken);
                }
                catch (FullStackApiException exception)
                {
                    Console.WriteLine(
                        $"Loot fixture account logout failed with {exception.Code}: {exception.Message}");
                }
            }
        }
    }

    public async Task<FullStackCharacterInventoryResponse?> RefreshInventoryAsync(
        int botIndex,
        CancellationToken cancellationToken)
    {
        if (!activeAccountSessions.TryGetValue(botIndex, out var session))
        {
            return null;
        }

        return await client.GetInventoryAsync(
            session.SessionToken,
            session.CharacterId,
            cancellationToken);
    }

    public void MarkSteadyState(long startTimestamp, TimeSpan plannedDuration)
    {
        gameServiceProcessSampler?.MarkSteadyState(startTimestamp, plannedDuration);
        postgresSampler.MarkSteadyState();
    }

    public StressProviderReport CaptureReport()
    {
        StressWorkerRegistration? capturedWorker;
        lock (workerGate)
        {
            capturedWorker = worker;
        }

        return new StressProviderReport(
            capturedWorker,
            null,
            new StressFullStackSummary(
                options.RunId,
                Volatile.Read(ref registeredBots),
                Volatile.Read(ref fixtureAccounts),
                Volatile.Read(ref inventoryFixtureBots),
                Volatile.Read(ref provisionedBots),
                Volatile.Read(ref loggedInBots),
                Volatile.Read(ref admittedBots),
                Volatile.Read(ref loggedOutBots),
                lootHotspotCorpseId,
                metrics.Capture())
            {
                ChurnLogouts = Volatile.Read(ref churnLogouts),
                ChurnLogins = Volatile.Read(ref churnLogins)
            },
            gameServiceProcessSampler?.CreateSummary(),
            postgresSampler.CreateSummary())
        {
            Storage = tablesAtSteadyStart is not null && tablesAtSteadyEnd is not null
                ? StressStorageGrowthSummary.Create(
                    tablesAtSteadyStart,
                    tablesAtSteadyEnd,
                    redisAtSteadyStart,
                    redisAtSteadyEnd,
                    steadyPlayers)
                : null,
            Drift = postgresSampler.CaptureDrift()
        };
    }

    public async ValueTask DisposeAsync()
    {
        gameServiceProcessSampler?.Dispose();
        concurrency.Dispose();
        httpClient.Dispose();
        await postgresSampler.DisposeAsync();
    }

    private void ValidateJoin(
        FullStackCharacterResponse character,
        FullStackJoinShardResponse join)
    {
        if (join.CharacterId != character.Id
            || !string.Equals(join.Shard.Id, options.ShardId, StringComparison.Ordinal)
            || !string.Equals(join.Shard.WorldId, options.WorldId, StringComparison.Ordinal)
            || !string.Equals(join.Endpoint.WorkerId, options.WorkerId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(join.Endpoint.RuntimeId)
            || string.IsNullOrWhiteSpace(join.Endpoint.Host)
            || join.Endpoint.UdpPort is <= 0 or > ushort.MaxValue
            || string.IsNullOrWhiteSpace(join.JoinTicket))
        {
            throw new FullStackApiException(
                "invalid_join_response",
                "GameService returned a join response that does not match the expected character, shard, World, or worker.",
                0);
        }

        // Like the Unity client, a bot never connects to a worker on another protocol version.
        if (join.Endpoint.ProtocolVersion != RealtimeProtocol.Version)
        {
            throw new FullStackApiException(
                "protocol_version_mismatch",
                $"SimulationWorker runs protocol version {join.Endpoint.ProtocolVersion}, but the bots require version {RealtimeProtocol.Version}.",
                0);
        }
    }

    private void CaptureWorker(FullStackJoinShardResponse join)
    {
        lock (workerGate)
        {
            if (worker is not null
                && (!string.Equals(
                        worker.WorkerId,
                        join.Endpoint.WorkerId,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        worker.RuntimeId,
                        join.Endpoint.RuntimeId,
                        StringComparison.Ordinal)))
            {
                throw new FullStackApiException(
                    "worker_placement_changed",
                    "GameService changed worker runtime placement during the stress run.",
                    0);
            }

            var observedAt = DateTime.UtcNow;
            worker = new StressWorkerRegistration(
                join.Endpoint.WorkerId,
                join.Endpoint.RuntimeId,
                join.Shard.FleetId,
                options.NodeId,
                join.Shard.Id,
                join.Shard.WorldId,
                join.Endpoint.Host,
                join.Endpoint.UdpPort,
                join.Shard.Capacity,
                join.Shard.ActivePlayers,
                join.Endpoint.ProtocolVersion,
                join.Endpoint.SimulationRevision,
                join.Endpoint.CollisionRevision,
                observedAt,
                observedAt,
                observedAt);
        }
    }

    private async Task TryLogoutAfterFailureAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.LogoutAsync(sessionToken, cancellationToken);
            Interlocked.Increment(ref loggedOutBots);
        }
        catch (Exception exception) when (
            exception is FullStackApiException
            or OperationCanceledException)
        {
            Console.WriteLine(
                $"Could not revoke a failed full-stack admission session: {exception.Message}");
        }
    }

    // Registration and login hash the password, which costs GameService a processor each. At 32
    // at once it took about eight processors of the reference machine and delayed the worker's
    // registry heartbeat past its timeout, so provisioning runs at most this many accounts at
    // once and the stack stays responsive while it is set up.
    public const int ProvisioningConcurrency = 8;

    // Players have accounts and characters before they play, so the playing workload registers
    // every account, creates its character, grants its kit, and logs it in before the first bot
    // joins. The ramp then measures only the way into the game: the character list, the Shards,
    // the join ticket, and the UDP join.
    private async Task ProvisionPlayingAccountsAsync(CancellationToken cancellationToken)
    {
        var scenario = options.Playing
            ?? throw new InvalidOperationException("The playing workload needs its scenario.");
        var assignments = PlayingRoster.Assign(scenario, options.BotCount, options.BotStartIndex, options.Seed);
        var started = Stopwatch.GetTimestamp();
        Console.WriteLine(
            $"Registering, equipping, and logging in {assignments.Count} accounts before the first bot joins.");
        await Parallel.ForEachAsync(
            assignments,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(options.HttpConcurrency, ProvisioningConcurrency)
            },
            async (assignment, token) =>
            {
                try
                {
                    provisionedAccounts[assignment.BotIndex] =
                        await ProvisionAsync(assignment, token);
                }
                catch (FullStackApiException exception)
                {
                    provisioningFailures[assignment.BotIndex] = exception.Code + ": " + exception.Message;
                }
            });
        Console.WriteLine(
            $"Provisioned {provisionedAccounts.Count} of {assignments.Count} accounts in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s.");
    }

    private async Task<ProvisionedAccount> ProvisionAsync(
        PlayingAssignment assignment,
        CancellationToken cancellationToken)
    {
        var username = Username(assignment.BotIndex);
        var registration = await client.RegisterAsync(
            $"{username}@stack-stress.local",
            username,
            password,
            cancellationToken,
            paceRateLimit: true);
        Interlocked.Increment(ref registeredBots);
        var character = await client.CreateCharacterAsync(
            registration.SessionToken,
            $"Stack {options.RunId} {assignment.BotIndex}",
            cancellationToken);
        Interlocked.Increment(ref provisionedBots);
        FullStackCharacterInventoryResponse? inventory = null;
        if (assignment.Kit is not null)
        {
            var kit = await client.SeedKitAsync(
                registration.SessionToken,
                character.Id,
                assignment.Kit,
                options.RunId,
                options.FixtureSecret!,
                cancellationToken);
            inventory = kit.Inventory;
            Interlocked.Increment(ref inventoryFixtureBots);
        }

        await client.LogoutAsync(registration.SessionToken, cancellationToken);
        var login = await client.LoginAsync(username, password, cancellationToken, paceRateLimit: true);
        Interlocked.Increment(ref loggedInBots);
        return new ProvisionedAccount(login.AccountId, login.SessionToken, character, inventory);
    }

    private async Task<StressAdmissionResult> AdmitProvisionedAsync(
        int botIndex,
        CancellationToken cancellationToken)
    {
        if (!provisionedAccounts.TryRemove(botIndex, out var account))
        {
            return StressAdmissionResult.Failure(
                "stress_provisioning_failed",
                provisioningFailures.GetValueOrDefault(botIndex) ?? $"Bot {botIndex} has no provisioned account.");
        }

        await concurrency.WaitAsync(cancellationToken);
        try
        {
            var characters = await client.ListCharactersAsync(account.SessionToken, cancellationToken);
            if (!characters.Any(candidate => candidate.Id == account.Character.Id))
            {
                throw new FullStackApiException(
                    "character_missing_after_login",
                    $"Character {account.Character.Id} was not returned after login.",
                    0);
            }

            var shards = await client.ListShardsAsync(cancellationToken);
            if (!shards.Any(candidate =>
                    string.Equals(candidate.Id, options.ShardId, StringComparison.Ordinal) && candidate.IsOnline))
            {
                throw new FullStackApiException(
                    "stress_shard_unavailable",
                    $"Shard '{options.ShardId}' was not online during admission.",
                    0);
            }

            var join = await client.JoinShardAsync(
                account.SessionToken,
                options.ShardId,
                account.Character.Id,
                cancellationToken);
            ValidateJoin(account.Character, join);
            CaptureWorker(join);
            activeAccountSessions[botIndex] = new ActiveAccountSession(account.SessionToken, account.Character.Id);
            admittedCharacters[botIndex] = new AdmittedCharacter(account.AccountId, account.Character);
            Interlocked.Increment(ref admittedBots);
            return StressAdmissionResult.Success(new StressBotAdmission(
                botIndex,
                account.AccountId,
                account.Character.Id,
                account.Character.Name,
                join.JoinTicket,
                join.ExpiresAt,
                join.Endpoint.Host,
                join.Endpoint.UdpPort,
                join.Shard.Id,
                join.Shard.WorldId,
                account.Inventory));
        }
        catch (FullStackApiException exception)
        {
            await TryLogoutAfterFailureAsync(account.SessionToken, cancellationToken);
            return StressAdmissionResult.Failure(exception.Code, exception.Message);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private string Username(int botIndex)
    {
        return $"stress_{options.RunId}_{botIndex}";
    }

    private sealed record ActiveAccountSession(string SessionToken, Guid CharacterId);

    private sealed record AdmittedCharacter(Guid AccountId, FullStackCharacterResponse Character);

    private sealed record ProvisionedAccount(
        Guid AccountId,
        string SessionToken,
        FullStackCharacterResponse Character,
        FullStackCharacterInventoryResponse? Inventory);
}
