using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ShooterMmo.Tools.StackStressGenerator;

public sealed class FullStackStressAdmissionProvider : IStressAdmissionProvider
{
    private readonly StressGeneratorOptions options;
    private readonly HttpClient httpClient;
    private readonly FullStackStressMetrics metrics;
    private readonly FullStackStressClient client;
    private readonly StressPostgresSampler postgresSampler;
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<int, ActiveAccountSession> activeAccountSessions = [];
    private readonly object workerGate = new();
    private readonly string password;
    private StressProcessSampler? authServiceProcessSampler;
    private StressWorkerRegistration? worker;
    private int registeredBots;
    private int fixtureAccounts;
    private int inventoryFixtureBots;
    private int provisionedBots;
    private int loggedInBots;
    private int admittedBots;
    private int loggedOutBots;
    private Guid? lootHotspotCorpseId;

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
            options.ConfirmedDisposableDatabase!);
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
                    ? $"Shard '{options.ShardId}' was not returned by AuthService."
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
        authServiceProcessSampler = StressProcessSampler.TryAttach("AuthService");
        authServiceProcessSampler?.Sample();

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
                lootHotspotCorpseId));
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

        await postgresSampler.VerifyAccountCountAsync(
            Volatile.Read(ref registeredBots) + Volatile.Read(ref fixtureAccounts),
            cancellationToken);
        await postgresSampler.VerifyGameplayInvariantsAsync(
            options.Workload,
            Volatile.Read(ref inventoryFixtureBots),
            lootHotspotCorpseId is not null,
            cancellationToken);
        await postgresSampler.SampleAsync(cancellationToken);
        authServiceProcessSampler?.Sample();
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        authServiceProcessSampler?.Sample();
        await postgresSampler.SampleAsync(cancellationToken);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
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
            var requestedLifetime = options.SteadyDuration
                + TimeSpan.FromTicks(options.RampInterval.Ticks * rampSteps)
                + TimeSpan.FromMinutes(5);
            var fixture = await client.CreateLootHotspotAsync(
                registration.SessionToken,
                new FullStackStackStressLootHotspotRequest(
                    options.RunId,
                    join.Endpoint.WorkerId,
                    join.Endpoint.RuntimeId,
                    join.Shard.Id,
                    options.LootHotspotX,
                    options.LootHotspotY,
                    options.LootHotspotZ,
                    Math.Clamp(requestedLifetime.TotalSeconds, 60d, 86_400d)),
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

    public void MarkSteadyState()
    {
        authServiceProcessSampler?.MarkSteadyState();
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
                metrics.Capture()),
            authServiceProcessSampler?.CreateSummary(),
            postgresSampler.CreateSummary());
    }

    public async ValueTask DisposeAsync()
    {
        authServiceProcessSampler?.Dispose();
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
                "AuthService returned a join response that does not match the expected character, shard, World, or worker.",
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
                    "AuthService changed worker runtime placement during the stress run.",
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

    private sealed record ActiveAccountSession(string SessionToken, Guid CharacterId);
}
