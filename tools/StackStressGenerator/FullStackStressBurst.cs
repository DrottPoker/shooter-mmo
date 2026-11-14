using System.Collections.Concurrent;
using System.Diagnostics;

namespace Mmorpg.Tools.StackStressGenerator;

public enum StressBurstKind
{
    Login,
    Logout
}

// A burst of the run: Accounts accounts that were registered before the ramp, and so are logged
// in, all log in again or all log out at once, At into the steady interval; each must end logged
// in or out, after its waits for auth_busy, within Deadline of the burst's start.
public sealed record StressBurstOptions(StressBurstKind Kind, int Accounts, TimeSpan At, TimeSpan Deadline)
{
    public string KindName => FormatKind(Kind);

    public static string FormatKind(StressBurstKind kind)
    {
        return kind == StressBurstKind.Login ? "login" : "logout";
    }
}

// The burst a run planned, in its configuration, so the verdict knows that one was due.
public sealed record StressBurstPlan(string Kind, int Accounts, double AtSeconds, double DeadlineSeconds);

// What a burst did. The seconds of an account run from the burst's start to its success, so they
// hold its waits for auth_busy. Refusals counts every auth_busy answer, Retries the ones that were
// waited out and sent again; a refusal after the last wait is a failure with the code auth_busy.
public sealed record StressBurstSummary(
    string Kind,
    int Accounts,
    int Provisioned,
    double StartedAfterSteadySeconds,
    double DeadlineSeconds,
    double SendSpreadMilliseconds,
    int Completed,
    int CompletedInTime,
    int Incomplete,
    int RefusedAccounts,
    long Refusals,
    long Retries,
    double? MedianSeconds,
    double? LongestSeconds,
    IReadOnlyDictionary<string, int> FailureCodes)
{
    // The burst accounts that logged out when the run ended, a login burst's new sessions and
    // whatever a logout burst left.
    public int LoggedOutAfter { get; init; }
}

// Registers the burst's accounts before the ramp, fires the burst, and logs out what is left.
public sealed class FullStackStressBurst(
    FullStackStressClient client,
    StressBurstOptions options,
    string runId,
    string password,
    int cleanupConcurrency)
{
    private readonly ConcurrentDictionary<int, string> sessions = new();
    private readonly ConcurrentDictionary<int, string> provisioningFailures = new();
    private int registered;
    private int loggedOutAfter;
    private StressBurstSummary? summary;

    public StressBurstOptions Options => options;

    public int RegisteredAccounts => Volatile.Read(ref registered);

    public StressBurstSummary? Summary => summary is null ? null : summary with { LoggedOutAfter = Volatile.Read(ref loggedOutAfter) };

    public static string Username(string runId, int index)
    {
        return $"stress_{runId}_b{index}";
    }

    // Registration leaves every account logged in. It runs as many at once as the provisioning
    // of the playing workload, so the stack stays responsive while it is set up.
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Console.WriteLine($"Registering {options.Accounts} accounts for the {options.KindName} burst.");
        await Parallel.ForEachAsync(
            Enumerable.Range(1, options.Accounts),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = FullStackStressAdmissionProvider.ProvisioningConcurrency
            },
            async (index, token) =>
            {
                var username = Username(runId, index);
                try
                {
                    var registration = await client.RegisterAsync(
                        $"{username}@stack-stress.local",
                        username,
                        password,
                        token,
                        paceRateLimit: true);
                    Interlocked.Increment(ref registered);
                    sessions[index] = registration.SessionToken;
                }
                catch (FullStackApiException exception)
                {
                    provisioningFailures[index] = exception.Code;
                }
            });
        Console.WriteLine(
            $"Registered {sessions.Count} of {options.Accounts} burst accounts in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s.");
    }

    // Every account waits at one gate, so all first requests leave together.
    public async Task RunAsync(double startedAfterSteadySeconds, CancellationToken cancellationToken)
    {
        var indexes = sessions.Keys.Order().ToArray();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new AccountResult[indexes.Length];
        var accounts = indexes
            .Select((index, slot) => Task.Run(
                async () =>
                {
                    await gate.Task;
                    results[slot] = await RunAccountAsync(index, cancellationToken);
                },
                cancellationToken))
            .ToArray();
        Console.WriteLine(
            $"{char.ToUpperInvariant(options.KindName[0])}{options.KindName[1..]} burst: {indexes.Length} accounts at once, {startedAfterSteadySeconds:0.0} s into the steady interval.");
        var startedAt = Stopwatch.GetTimestamp();
        gate.SetResult();
        await Task.WhenAll(accounts);

        summary = Summarize(results, startedAt, startedAfterSteadySeconds);
        Console.WriteLine(
            $"The {options.KindName} burst ended: {summary.Completed} of {options.Accounts} completed, {summary.CompletedInTime} within {options.Deadline.TotalSeconds:0} s, "
            + $"{summary.Refusals} auth_busy refusals and {summary.Retries} retries, longest {summary.LongestSeconds?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"} s, "
            + $"first requests sent within {summary.SendSpreadMilliseconds:0} ms.");
    }

    // Logs out every burst account that still holds a session when the run ends.
    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            sessions.ToArray(),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = cleanupConcurrency },
            async (pair, token) =>
            {
                try
                {
                    await client.LogoutAsync(pair.Value, token);
                    sessions.TryRemove(pair.Key, out _);
                    Interlocked.Increment(ref loggedOutAfter);
                }
                catch (FullStackApiException exception)
                {
                    Console.WriteLine($"Burst account {pair.Key} logout failed with {exception.Code}: {exception.Message}");
                }
            });
    }

    private async Task<AccountResult> RunAccountAsync(int index, CancellationToken cancellationToken)
    {
        var sent = Stopwatch.GetTimestamp();
        var retries = 0;
        try
        {
            if (options.Kind == StressBurstKind.Login)
            {
                var login = await client.LoginAsync(
                    Username(runId, index),
                    password,
                    cancellationToken,
                    paceRateLimit: true,
                    busyWaited: () => retries++);
                sessions[index] = login.SessionToken;
            }
            else
            {
                await client.LogoutAsync(sessions[index], cancellationToken);
                sessions.TryRemove(index, out _);
            }

            return new AccountResult(sent, Stopwatch.GetTimestamp(), retries, null);
        }
        catch (FullStackApiException exception)
        {
            return new AccountResult(sent, Stopwatch.GetTimestamp(), retries, exception.Code);
        }
    }

    private StressBurstSummary Summarize(AccountResult[] results, long startedAt, double startedAfterSteadySeconds)
    {
        var completed = results
            .Where(result => result.FailureCode is null)
            .Select(result => Stopwatch.GetElapsedTime(startedAt, result.Ended).TotalSeconds)
            .Order()
            .ToArray();
        var failureCodes = results
            .Where(result => result.FailureCode is not null)
            .Select(result => result.FailureCode!)
            .Concat(provisioningFailures.Values.Select(code => "provisioning_" + code))
            .GroupBy(code => code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var finalRefusals = results.Count(result => result.FailureCode == FullStackStressClient.AuthBusyCode);
        var retries = results.Sum(result => (long)result.Retries);
        var inTime = completed.Count(seconds => seconds <= options.Deadline.TotalSeconds);
        return new StressBurstSummary(
            options.KindName,
            options.Accounts,
            results.Length,
            startedAfterSteadySeconds,
            options.Deadline.TotalSeconds,
            results.Length == 0
                ? 0
                : Stopwatch.GetElapsedTime(results.Min(result => result.Sent), results.Max(result => result.Sent)).TotalMilliseconds,
            completed.Length,
            inTime,
            options.Accounts - inTime,
            results.Count(result => result.Retries > 0 || result.FailureCode == FullStackStressClient.AuthBusyCode),
            retries + finalRefusals,
            retries,
            completed.Length == 0 ? null : completed[(completed.Length - 1) / 2],
            completed.Length == 0 ? null : completed[^1],
            failureCodes);
    }

    private readonly record struct AccountResult(long Sent, long Ended, int Retries, string? FailureCode);
}
