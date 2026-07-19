using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ShooterMmo.Tools.StackStressGenerator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (StressGeneratorOptions.IsHelpRequested(args))
        {
            Console.WriteLine(StressGeneratorOptions.HelpText);
            return 0;
        }

        StressGeneratorOptions options;
        try
        {
            options = StressGeneratorOptions.Parse(args, Directory.GetCurrentDirectory());
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
            var report = options.Mode == StressRunMode.WorkerOnly
                ? await RunWorkerOnlyAsync(options, args, shutdown.Token)
                : await RunFullStackAsync(options, shutdown.Token);
            await report.WriteAsync(options.OutputPath, shutdown.Token);
            WriteFinalSummary(report, options.OutputPath);
            return report.Bots.FailedBots == 0
                && report.Bots.JoinedBots == options.BotCount
                && report.Bots.CompletedBots == options.BotCount
                && (report.FullStack is null
                    || report.FullStack.LoggedOutBots == report.FullStack.AdmittedBots)
                ? 0
                : 1;
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

    private static async Task<StressRunReport> RunWorkerOnlyAsync(
        StressGeneratorOptions options,
        string[] args,
        CancellationToken cancellationToken)
    {
        var authority = new StressAuthorityState(options);
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(options.AuthorityUrl.ToString().TrimEnd('/'));
        builder.Services.AddSingleton(authority);
        builder.Services.Configure<JsonOptions>(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        app.MapStressAuthority();
        await using var provider = new WorkerOnlyStressAdmissionProvider(options, authority);

        try
        {
            await app.StartAsync(cancellationToken);
            WriteWorkerOnlyStartupInstructions(options);
            var coordinator = new StressRunCoordinator(options, provider);
            return await coordinator.RunAsync(cancellationToken);
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
        Console.WriteLine($"$env:AUTH_SERVICE_BASE_URL='{options.AuthorityUrl}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_SERVICE_SECRET='{options.WorkerSecret}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_MAX_CONNECTIONS='{Math.Max(100, options.BotCount)}'");
        Console.WriteLine($"$env:SIMULATION_WORLD_ID='{options.WorldId}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_UDP_PORT='{options.WorkerUdpPort}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_ADVERTISED_HOST='{options.WorkerHost}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_ADVERTISED_UDP_PORT='{options.WorkerUdpPort}'");
        Console.WriteLine("dotnet run --project SimulationWorker --configuration Release");
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
        WriteProcessSummary("AuthService", report.AuthServiceProcess);
        WriteProcessSummary("Generator", report.GeneratorProcess);

        if (report.FullStack is not null)
        {
            Console.WriteLine(
                $"Full-stack lifecycle: {report.FullStack.RegisteredBots} registered, {report.FullStack.ProvisionedBots} provisioned, {report.FullStack.LoggedInBots} logged in, {report.FullStack.AdmittedBots} admitted, {report.FullStack.LoggedOutBots} logged out.");
        }

        if (report.Postgres is not null)
        {
            Console.WriteLine(
                $"PostgreSQL: {report.Postgres.CommittedTransactions} commits, {report.Postgres.RolledBackTransactions} rollbacks, max {report.Postgres.MaximumConnections} connections, max {report.Postgres.MaximumWaitingConnections} active waits, {report.Postgres.SimulationSessionRowsUpdated} simulation-session updates, {report.Postgres.MaximumExpiredUnreleasedSimulationSessions} expired unreleased sessions, {report.Postgres.Deadlocks} deadlocks.");
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
