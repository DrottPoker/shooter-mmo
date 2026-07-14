using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ShooterMmo.Tools.SimulationStressGenerator;

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
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();
        app.MapStressAuthority();

        try
        {
            await app.StartAsync(shutdown.Token);
            WriteStartupInstructions(options);
            var coordinator = new StressRunCoordinator(options, authority);
            var report = await coordinator.RunAsync(shutdown.Token);
            await report.WriteAsync(options.OutputPath, shutdown.Token);
            WriteFinalSummary(report, options.OutputPath);
            return report.Bots.FailedBots == 0
                && report.Bots.JoinedBots == options.BotCount
                && report.Bots.CompletedBots == options.BotCount
                ? 0
                : 1;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            Console.Error.WriteLine("Stress run cancelled.");
            return 130;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                "SimulationWorker did not register before the configured worker wait timeout.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stress run failed: {exception.Message}");
            return 1;
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private static void WriteStartupInstructions(StressGeneratorOptions options)
    {
        Console.WriteLine($"Stress authority listening at {options.AuthorityUrl}.");
        Console.WriteLine("Start SimulationWorker in a separate PowerShell terminal with:");
        Console.WriteLine($"$env:AUTH_SERVICE_BASE_URL='{options.AuthorityUrl}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_SERVICE_SECRET='{options.WorkerSecret}'");
        Console.WriteLine($"$env:SIMULATION_WORKER_MAX_CONNECTIONS='{Math.Max(100, options.BotCount)}'");
        Console.WriteLine("dotnet run --project SimulationWorker --configuration Release");
        Console.WriteLine(
            "The secret is ephemeral for this stress run and is not written to the JSON report.");
    }

    private static void WriteFinalSummary(StressRunReport report, string outputPath)
    {
        var ack = report.Bots.InputAcknowledgementLatency;
        Console.WriteLine(
            $"Stress run complete: {report.Bots.JoinedBots}/{report.Bots.RequestedBots} joined, {report.Bots.CompletedBots} left cleanly, {report.Bots.FailedBots} failed.");
        Console.WriteLine(
            $"Traffic: {report.Bots.PacketsSent} packets and {report.Bots.BytesSent} bytes sent, {report.Bots.PacketsReceived} packets and {report.Bots.BytesReceived} bytes received.");
        if (ack is not null)
        {
            Console.WriteLine(
                $"Input acknowledgement latency: p50 {ack.P50Ms:0.0} ms, p95 {ack.P95Ms:0.0} ms, p99 {ack.P99Ms:0.0} ms, max {ack.MaximumMs:0.0} ms.");
        }

        if (report.WorkerProcess is not null)
        {
            Console.WriteLine(
                $"Worker process: avg single-core equivalent CPU {report.WorkerProcess.AverageSingleCoreCpuPercent:0.0}%, max {report.WorkerProcess.MaximumSingleCoreCpuPercent:0.0}%; avg whole-machine CPU {report.WorkerProcess.AverageMachineCpuPercent:0.0}%; steady working-set windows {report.WorkerProcess.SteadyStateFirstWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} to {report.WorkerProcess.SteadyStateLastWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} MiB, trend {report.WorkerProcess.SteadyStateWorkingSetTrendBytesPerMinute / 1024d / 1024d:0.0} MiB/min, max {report.WorkerProcess.SteadyStateMaximumWorkingSetBytes / 1024d / 1024d:0.0} MiB.");
        }

        if (report.GeneratorProcess is not null)
        {
            Console.WriteLine(
                $"Generator process: avg single-core equivalent CPU {report.GeneratorProcess.AverageSingleCoreCpuPercent:0.0}%, max {report.GeneratorProcess.MaximumSingleCoreCpuPercent:0.0}%; steady working-set windows {report.GeneratorProcess.SteadyStateFirstWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} to {report.GeneratorProcess.SteadyStateLastWindowAverageWorkingSetBytes / 1024d / 1024d:0.0} MiB, trend {report.GeneratorProcess.SteadyStateWorkingSetTrendBytesPerMinute / 1024d / 1024d:0.0} MiB/min, max {report.GeneratorProcess.SteadyStateMaximumWorkingSetBytes / 1024d / 1024d:0.0} MiB.");
        }

        Console.WriteLine($"JSON report: {outputPath}");
    }
}
