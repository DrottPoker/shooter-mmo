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
            : !PassedOwnChecks(report, options)
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
    internal static bool PassedOwnChecks(StressRunReport report, StressGeneratorOptions options)
    {
        return report.Bots.FailedBots == 0
            && report.Bots.JoinedBots == options.BotCount
            && report.Bots.CompletedBots == options.BotCount
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
                report.SteadyEndedAt)
        };
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
