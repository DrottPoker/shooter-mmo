using Mmorpg.Tools.StackStressGenerator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "plan")
        {
            return WritePlan(args[1..]);
        }

        if (args.Length > 0 && args[0] == "world-variant")
        {
            return WriteWorldVariant(args[1..]);
        }

        if (args.Length > 0 && args[0] == "judge")
        {
            return await JudgeAsync(args[1..]);
        }

        if (StressGeneratorOptions.IsHelpRequested(args))
        {
            Console.WriteLine(StressGeneratorOptions.HelpText);
            return 0;
        }

        StressGeneratorOptions options;
        StressQualityBoundary? boundary = null;
        try
        {
            options = StressGeneratorOptions.Parse(args, Directory.GetCurrentDirectory());
            if (options.QualityBoundaryPath is not null)
            {
                boundary = StressQualityBoundary.Load(options.QualityBoundaryPath);
            }
        }
        catch (StressGeneratorOptionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Use --help to list supported options.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            return options.Mode == StressRunMode.WorkerOnly
                ? await RunWorkerOnlyAsync(
                    options,
                    args,
                    report => FinishAsync(report, options, boundary, shutdown.Token),
                    shutdown.Token)
                : await FinishAsync(
                    await RunFullStackAsync(options, shutdown.Token),
                    options,
                    boundary,
                    shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine("Stack stress run cancelled.");
            return 130;
        }
        catch (TimeoutException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stack stress run failed: {exception.Message}");
            return 1;
        }
    }

    // Adds the worker's numbers and the verdict, writes the report and the summary, and returns
    // the exit code.
    private static async Task<int> FinishAsync(
        StressRunReport report,
        StressGeneratorOptions options,
        StressQualityBoundary? boundary,
        CancellationToken cancellationToken)
    {
        report = await AddWorkerPerformanceAsync(report, options, cancellationToken);
        if (boundary is not null)
        {
            report = report with { Verdict = boundary.Evaluate(report) };
        }

        await report.WriteAsync(options.OutputPath, cancellationToken);
        WriteFinalSummary(report, options.OutputPath);
        var exitCode = report.Verdict is { Valid: false }
            ? CouldNotMeasureExitCode
            : !PassedOwnChecks(report)
                ? 1
                : report.Verdict is { Passed: false }
                    ? 3
                    : 0;
        if (options.SummaryPath is not null)
        {
            await StressRunSummary.Create(report, options, exitCode, StressMachine.Current())
                .WriteAsync(options.SummaryPath, cancellationToken);
            Console.WriteLine($"Summary: {options.SummaryPath}");
        }

        return exitCode;
    }

    // The generator fell behind, so the run measured nothing; it is neither passed nor failed.
    public const int CouldNotMeasureExitCode = 4;

    // The generator's own checks, which decide exit code 1 as before the quality boundary.
    internal static bool PassedOwnChecks(StressRunReport report)
    {
        var bots = report.Configuration.BotCount;
        return report.Bots.FailedBots == 0
            && report.Bots.JoinedBots == bots
            && report.Bots.CompletedBots == bots
            && (report.FullStack is null
                || report.FullStack.LoggedOutBots == report.FullStack.AdmittedBots)
            && report.Gameplay.UnexpectedFailures == 0
            && (report.Postgres?.ItemInvariants?.Violations ?? 0) == 0;
    }

    private static int WritePlan(string[] args)
    {
        try
        {
            var (scenarioPath, request) = StressLoadPlanRequest.Parse(args);
            var workingDirectory = Directory.GetCurrentDirectory();
            var scenario = StressLoadScenario.Load(Path.GetFullPath(scenarioPath, workingDirectory));
            Console.WriteLine(StressLoadPlan.Create(scenario, request, workingDirectory).ToJson());
            return 0;
        }
        catch (StressGeneratorOptionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    // StackStressGenerator world-variant --scenario <file> --run-folder <dir>: writes the
    // scenario's World variant into <dir>/world-variant and says whether it keeps the checked-in
    // collision revision, so a Unity client can still join the run.
    private static int WriteWorldVariant(string[] args)
    {
        try
        {
            if (args.Length != 4 || args[0] != "--scenario" || args[2] != "--run-folder")
            {
                throw new StressGeneratorOptionException(
                    "Usage: StackStressGenerator world-variant --scenario <file> --run-folder <dir>");
            }

            var workingDirectory = Directory.GetCurrentDirectory();
            var scenario = StressLoadScenario.Load(Path.GetFullPath(args[1], workingDirectory));
            var variantRoot = Path.Combine(
                Path.GetFullPath(args[3], workingDirectory),
                StressLoadPlan.WorldVariantFolderName);
            var log = Path.Combine(Path.GetFullPath(args[3], workingDirectory), "world-variant.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            using var logWriter = new StreamWriter(log);
            var result = StressWorldVariant.Write(
                scenario,
                variantRoot,
                Mmorpg.WorldData.Content.ContentLayout.FindRepositoryRoot(workingDirectory),
                logWriter);
            Console.WriteLine(
                $"World variant of {result.WorldId}: {result.AddedInstances} added actors on walkable ground, spawn revision {result.SpawnRevision}.");
            Console.WriteLine(result.KeepsCollisionRevision
                ? $"The variant keeps the checked-in collision revision {result.CollisionRevision}, so a Unity client can join this run."
                : $"The variant's collision revision {result.CollisionRevision} differs from the checked-in {result.CheckedInCollisionRevision}, because it adds resource nodes; a Unity client cannot join this run.");
            return 0;
        }
        catch (StressGeneratorOptionException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The World variant could not be written: {exception.Message}");
            return 2;
        }
    }

    // Folds the worker's performance file into the report. The file is read once it covers the
    // end of the run, at most one metrics interval and a few seconds later.
    private static async Task<StressRunReport> AddWorkerPerformanceAsync(
        StressRunReport report,
        StressGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (options.WorkerPerformanceFilePath is null)
        {
            return report;
        }

        Console.WriteLine("Waiting for the worker's performance interval that covers the end of the run.");
        var intervals = await StressWorkerPerformance.ReadAfterRunAsync(
            options.WorkerPerformanceFilePath,
            report.EndedAt,
            TimeSpan.FromSeconds(75),
            cancellationToken);
        return report with
        {
            WorkerPerformance = StressWorkerPerformance.Fold(
                intervals,
                report.StartedAt,
                report.EndedAt,
                report.SteadyStartedAt,
                report.SteadyEndedAt),
            Soak = AddWorkerMemory(
                report.Soak,
                StressWorkerMemory.Create(intervals, report.SteadyStartedAt, report.SteadyEndedAt))
        };
    }

    // The worker's own file adds its managed heap, its generation 2 collections, and its
    // allocation to the working set that the process sampler measured once a second; without
    // the sampler, the file's working set stands in.
    internal static StressSoakSummary? AddWorkerMemory(StressSoakSummary? soak, StressMemoryTrend? fromFile)
    {
        if (fromFile is null)
        {
            return soak;
        }

        soak ??= new StressSoakSummary(null, null, []);
        var sampled = soak.Memory.FirstOrDefault(trend => trend.Process == fromFile.Process);
        var merged = sampled is null
            ? fromFile
            : sampled with
            {
                ManagedHeapSlopeMiBPerHour = fromFile.ManagedHeapSlopeMiBPerHour,
                ManagedHeapFifthsMiB = fromFile.ManagedHeapFifthsMiB,
                Generation2CollectionsPerHour = fromFile.Generation2CollectionsPerHour,
                AllocatedBytesPerSecond = fromFile.AllocatedBytesPerSecond
            };
        return soak with
        {
            Memory = [merged, .. soak.Memory.Where(trend => trend.Process != fromFile.Process)]
        };
    }

    // StackStressGenerator judge --run-folder <dir> [--quality-boundary <file>]: once the
    // services stopped, counts the warning, error, and critical lines of their logs by message
    // class, notes the PostgreSQL log the run kept, judges the report again, and rewrites the
    // report and the summary. Exit codes: 0 passed, 1 failed, 4 could not measure, 2 when the
    // report cannot be judged.
    private static async Task<int> JudgeAsync(string[] args)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index + 1 < args.Length; index += 2)
            {
                if (args[index] is not ("--run-folder" or "--quality-boundary") || !values.TryAdd(args[index], args[index + 1]))
                {
                    throw new StressGeneratorOptionException($"The judge argument '{args[index]}' is unknown or given twice.");
                }
            }

            if (args.Length % 2 != 0 || !values.TryGetValue("--run-folder", out var folder))
            {
                throw new StressGeneratorOptionException(
                    "Usage: StackStressGenerator judge --run-folder <dir> [--quality-boundary <file>]");
            }

            var workingDirectory = Directory.GetCurrentDirectory();
            var runFolder = Path.GetFullPath(folder, workingDirectory);
            var boundary = StressQualityBoundary.Load(Path.GetFullPath(
                values.GetValueOrDefault("--quality-boundary", StressLoadPlanRequest.DefaultQualityBoundaryPath),
                workingDirectory));
            var reportPath = Path.Combine(runFolder, "generator-report.json");
            var summaryPath = Path.Combine(runFolder, "summary.json");
            var report = await StressRunReport.ReadAsync(reportPath, CancellationToken.None);
            var summary = StressRunSummary.Parse(await File.ReadAllTextAsync(summaryPath));
            report = StressRunJudge.AddLogs(report, runFolder, boundary.LogAllowances);
            report = report with { Verdict = boundary.Evaluate(report) };
            var exitCode = report.Verdict is { Valid: false }
                ? CouldNotMeasureExitCode
                : !PassedOwnChecks(report) || !report.Verdict.Passed
                    ? 1
                    : 0;
            await report.WriteAsync(reportPath, CancellationToken.None);
            await (StressRunSummary.Create(report, summary.Scenario, summary.Commit, summary.TreeDirty, exitCode, summary.Machine) with
            {
                Date = summary.Date
            }).WriteAsync(summaryPath, CancellationToken.None);
            WriteServiceLogs(report.ServiceLogs!);
            if (report.PostgresLog is { } log)
            {
                Console.WriteLine(
                    $"PostgreSQL log {log.Path}: {log.DeadlockReports} deadlock reports, {log.LockWaitReports} lock waits longer than the deadlock timeout.");
            }

            var logRule = report.Verdict.Rules.Single(rule => rule.Rule == "service_log_errors");
            Console.WriteLine(
                $"Quality rule service_log_errors: {(logRule.Passed ? "passed" : "FAILED")}, measured {logRule.Measured?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"} {logRule.Unit}, limit {logRule.Comparison} {logRule.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(logRule.Note is null ? string.Empty : $" ({logRule.Note})")}.");
            return exitCode;
        }
        catch (Exception exception) when (exception is StressGeneratorOptionException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"The run cannot be judged: {exception.Message}");
            return 2;
        }
    }

    private static void WriteServiceLogs(StressServiceLogSummary logs)
    {
        foreach (var file in logs.Files)
        {
            Console.WriteLine(
                $"Service log {file.Service} ({Path.GetFileName(file.Path)}): {file.Entries} entries, {file.Warnings} warnings, {file.Errors} errors, {file.Critical} critical.");
        }

        foreach (var entry in logs.Classes)
        {
            Console.WriteLine(
                $"  {entry.Count,6} {entry.Level,-8} {entry.Service} {entry.Category}: {entry.Message}{(entry.Allowed ? $" (allowed: {entry.AllowReason})" : string.Empty)}");
        }
    }

    private static async Task<int> RunWorkerOnlyAsync(
        StressGeneratorOptions options,
        string[] args,
        Func<StressRunReport, Task<int>> finish,
        CancellationToken cancellationToken)
    {
        var authority = new StressAuthorityState(options);
        var app = StressAuthorityHost.Create(
            authority,
            options.AuthorityUrl.ToString().TrimEnd('/'),
            args);
        await using var provider = new WorkerOnlyStressAdmissionProvider(options, authority);

        try
        {
            await app.StartAsync(cancellationToken);
            WriteWorkerOnlyStartupInstructions(options);
            var coordinator = new StressRunCoordinator(options, provider);
            var exitCode = await finish(await coordinator.RunAsync(cancellationToken));
            if (options.AuthorityHoldSeconds > 0)
            {
                // The worker still reaches its authority while it stops, so its shutdown runs as
                // against GameService instead of waiting for requests that cannot succeed.
                Console.WriteLine(
                    $"Keeping the authority up for at most {options.AuthorityHoldSeconds} seconds, until Ctrl+C, while the worker stops.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.AuthorityHoldSeconds), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("The authority stops.");
                }
            }

            return exitCode;
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private static async Task<StressRunReport> RunFullStackAsync(
        StressGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Full-stack mode targets {options.AuthorityUrl} and disposable database '{options.ConfirmedDisposableDatabase}'.");
        await using var provider = new FullStackStressAdmissionProvider(options);
        var coordinator = new StressRunCoordinator(options, provider);
        return await coordinator.RunAsync(cancellationToken);
    }

    private static void WriteWorkerOnlyStartupInstructions(StressGeneratorOptions options)
    {
        Console.WriteLine($"Stress authority listening at {options.AuthorityUrl}.");
        Console.WriteLine("Start SimulationWorker in a separate PowerShell terminal with:");
        Console.WriteLine($"$env:GAME_SERVICE_BASE_URL='{options.AuthorityUrl}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_SERVICE_SECRET='{options.WorkerSecret}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_MAX_CONNECTIONS='{Math.Max(100, options.BotCount)}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_ID='{options.WorkerId}'");
        Console.WriteLine($"$env:SIMULATION_FLEET_ID='{options.FleetId}'");
        Console.WriteLine($"$env:SIMULATION_NODE_ID='{options.NodeId}'");
        Console.WriteLine($"$env:SIMULATION_SHARD_ID='{options.ShardId}'");
        Console.WriteLine($"$env:SIMULATION_WORLD_ID='{options.WorldId}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_UDP_PORT='{options.WorkerUdpPort}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_ADVERTISED_HOST='{options.WorkerHost}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_ADVERTISED_UDP_PORT='{options.WorkerUdpPort}'");
        Console.WriteLine(
            "dotnet run --project src/Server/SimulationWorker --configuration Release --no-build --no-launch-profile");
        Console.WriteLine(
            "The secret is ephemeral for this stress run and is not written to the JSON report.");
    }

    private static void WriteFinalSummary(StressRunReport report, string outputPath)
    {
        var ack = report.Bots.InputAcknowledgementLatency;
        Console.WriteLine(
            $"Stack stress run complete: {report.Bots.JoinedBots}/{report.Bots.RequestedBots} joined, {report.Bots.CompletedBots} left cleanly, {report.Bots.FailedBots} failed.");
        Console.WriteLine(
            $"Traffic: {report.Bots.PacketsSent} packets and {report.Bots.BytesSent} bytes sent, {report.Bots.PacketsReceived} packets and {report.Bots.BytesReceived} bytes received.");
        if (ack is not null)
        {
            Console.WriteLine(
                $"Input acknowledgement latency: p50 {ack.P50Ms:0.0} ms, p95 {ack.P95Ms:0.0} ms, p99 {ack.P99Ms:0.0} ms, max {ack.MaximumMs:0.0} ms.");
        }

        WriteProcessSummary("Worker", report.WorkerProcess);
        WriteProcessSummary("GameService", report.GameServiceProcess);
        WriteProcessSummary("Generator", report.GeneratorProcess);

        if (report.FullStack is not null)
        {
            Console.WriteLine(
                $"Full-stack lifecycle: {report.FullStack.RegisteredBots} registered, {report.FullStack.FixtureAccounts} fixture accounts, {report.FullStack.InventoryFixtureBots} inventory fixtures, {report.FullStack.ProvisionedBots} provisioned, {report.FullStack.LoggedInBots} logged in, {report.FullStack.AdmittedBots} admitted, {report.FullStack.LoggedOutBots} logged out.");
        }

        if (report.Playing is { } playing)
        {
            Console.WriteLine(
                "Playing roles: " + string.Join(", ", playing.Roles.Select(role => $"{role.Value} {role.Key}"))
                + "; counters: " + string.Join(", ", playing.Counters.Where(counter => counter.Value != 0).Select(counter => $"{counter.Key} {counter.Value}"))
                + ".");
            foreach (var timing in playing.Timings.Where(timing => timing.Value is not null))
            {
                Console.WriteLine(
                    $"Playing timing {timing.Key}: {timing.Value!.Samples} samples, p50 {timing.Value.P50Ms:0.0} ms, p95 {timing.Value.P95Ms:0.0} ms, max {timing.Value.MaximumMs:0.0} ms.");
            }
        }

        foreach (var operation in report.Gameplay.Operations)
        {
            var latency = operation.Value.Latency;
            Console.WriteLine(
                $"Gameplay {operation.Key}: {operation.Value.Requests} requests, {operation.Value.Successes} successes, {operation.Value.ExpectedRejections} expected conflicts, {operation.Value.UnexpectedRejections} unexpected rejections, {operation.Value.Timeouts} timeouts, {operation.Value.IncompleteRequests} incomplete, p95 {(latency is null ? "n/a" : $"{latency.P95Ms:0.0} ms")}.");
        }

        if (report.Postgres is not null)
        {
            Console.WriteLine(
                $"PostgreSQL: {report.Postgres.CommittedTransactions} commits, {report.Postgres.RolledBackTransactions} rollbacks, max {report.Postgres.MaximumConnections} connections, max {report.Postgres.MaximumWaitingConnections} active waits, {report.Postgres.SimulationSessionRowsUpdated} simulation-session updates, {report.Postgres.MaximumExpiredUnreleasedSimulationSessions} expired unreleased sessions, {report.Postgres.Deadlocks} deadlocks.");
        }

        if (report.Postgres?.ItemInvariants is { } invariants)
        {
            Console.WriteLine(
                $"Item invariants: loot {invariants.ActualLootQuantity}/{invariants.ExpectedLootQuantity}, inventory item {invariants.ActualInventoryItemQuantity}/{invariants.ExpectedInventoryItemQuantity}, duplicate slots {invariants.DuplicateContainerSlots}, invalid custody {invariants.InvalidItemCustodyRows}, pending operations {invariants.PendingItemOperations}.");
        }

        WriteSoakSummary(report);

        if (report.Bots.RemoteCadence is { } cadence)
        {
            Console.WriteLine(
                $"Remote player updates per second during the steady interval: average {cadence.AverageHz:0.0}, p5 over bots {cadence.P5Hz:0.0}, minimum {cadence.MinimumHz:0.0}, over {cadence.Bots} bots and {cadence.Pairs} pairs.");
        }

        if (report.Verdict is { } verdict)
        {
            foreach (var rule in verdict.Rules)
            {
                Console.WriteLine(
                    $"Quality rule {rule.Rule}: {(rule.Passed ? "passed" : "FAILED")}, measured {(rule.Measured is { } measured ? measured.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "n/a")} {rule.Unit}, limit {rule.Comparison} {rule.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(rule.Note is null ? string.Empty : $" ({rule.Note})")}.");
            }

            Console.WriteLine(verdict.Valid
                ? $"Quality boundary {(verdict.Passed ? "passed" : "FAILED")}."
                : "The generator fell behind, so the run could not measure; see the validity rules.");
        }

        Console.WriteLine($"JSON report: {outputPath}");
    }

    private static void WriteSoakSummary(StressRunReport report)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (report.Playing?.DeathsPerBotPerTenMinutes is { Count: > 0 } deaths)
        {
            Console.WriteLine(
                "Deaths per bot and ten minutes of the steady interval: "
                + string.Join(", ", deaths.Select(role => string.Create(invariant, $"{role.Key} {role.Value:0.00}")))
                + ".");
        }

        if (report.Soak is not { } soak)
        {
            return;
        }

        foreach (var trend in soak.Memory)
        {
            Console.WriteLine(string.Create(
                invariant,
                $"Memory of the {trend.Process} over the steady interval: working set {trend.WorkingSetSlopeMiBPerHour:+0.0;-0.0} MiB/h, fifths {FormatFifths(trend.WorkingSetFifthsMiB)} MiB{(trend.ManagedHeapFifthsMiB is { } heap ? $"; managed heap {trend.ManagedHeapSlopeMiBPerHour:+0.0;-0.0} MiB/h, fifths {FormatFifths(heap)} MiB" : string.Empty)}{(trend.Generation2CollectionsPerHour is { } gen2 ? $"; {gen2:0} generation 2 collections per hour" : string.Empty)}{(trend.AllocatedBytesPerSecond is { } allocated ? $"; {allocated / 1024d / 1024d:0.0} MiB allocated per second" : string.Empty)}."));
        }

        if (soak.Drift is { } drift)
        {
            Console.WriteLine(string.Create(
                invariant,
                $"Worst drift of the steady interval: {drift.MaximumSessionsWithoutBot} sessions without a bot that may hold one and {drift.MaximumBotsWithoutSession} playing bots without a session, {drift.MaximumSessionsNearLeaseExpiry} sessions within {drift.LeaseExpiryThresholdSeconds:0} s of lease expiry, least lease left {(drift.MinimumSessionLeaseHeadroomSeconds is { } lease ? lease.ToString("0.0", invariant) + " s" : "n/a")}, worker registration headroom {(drift.MinimumWorkerRegistrationHeadroomSeconds is { } headroom ? headroom.ToString("0.0", invariant) + " s" : "n/a")}, {drift.MaximumPendingItemOperations} pending item operations, {drift.MaximumServiceConnectionsInUse} of {drift.MaximumServiceConnections} GameService connections in use{(drift.DatabasePoolSize is { } pool ? $" (pool {pool})" : string.Empty)}, oldest idle transaction {drift.MaximumIdleInTransactionSeconds:0.0} s."));
        }

        if (soak.Storage is { } storage)
        {
            Console.WriteLine(string.Create(
                invariant,
                $"Storage growth over {storage.Hours * 60d:0.0} minutes and {storage.Players} players, by bytes per hour ({storage.CountMethod}, {storage.StartCountMilliseconds:0} and {storage.EndCountMilliseconds:0} ms):"));
            foreach (var table in storage.Tables.Take(StressRunSummary.TopTables))
            {
                Console.WriteLine(string.Create(
                    invariant,
                    $"  {table.Table}: rows {table.StartRows} -> {table.EndRows}, {table.StartBytes / 1024d:0} -> {table.EndBytes / 1024d:0} KiB; {table.RowsPerHour:0} rows/h, {table.BytesPerHour / 1024d / 1024d:0.00} MiB/h, {table.RowsPerPlayerHour:0.0} rows and {table.BytesPerPlayerHour / 1024d:0.0} KiB per player hour"));
            }

            if (storage.Redis is { } redis)
            {
                Console.WriteLine(string.Create(
                    invariant,
                    $"  Redis: keys {redis.Start.Keys} -> {redis.End.Keys}, used memory {redis.Start.UsedMemoryBytes / 1024d / 1024d:0.0} -> {redis.End.UsedMemoryBytes / 1024d / 1024d:0.0} MiB; {redis.KeysPerHour:0} keys/h"));
            }
        }
    }

    private static string FormatFifths(IReadOnlyList<double?> fifths)
    {
        return string.Join(
            " / ",
            fifths.Select(value => value is { } number ? number.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "n/a"));
    }

    private static void WriteProcessSummary(
        string label,
        StressProcessSummary? process)
    {
        if (process is null)
        {
            return;
        }

        Console.WriteLine(
            $"{label} process: avg single-core equivalent CPU {process.AverageSingleCoreCpuPercent:0.0}%, max {process.MaximumSingleCoreCpuPercent:0.0}%; avg whole-machine CPU {process.AverageMachineCpuPercent:0.0}%; steady working-set windows {process.SteadyStateFirstWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} to {process.SteadyStateLastWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} MiB, trend {process.SteadyStateWorkingSetTrendBytesPerMinute / 1024d / 1024d:0.0} MiB/min, max {process.SteadyStateMaximumWorkingSetBytes / 1024d / 1024d:0.0} MiB.");
    }
}
