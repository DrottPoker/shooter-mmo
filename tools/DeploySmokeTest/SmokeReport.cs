namespace Mmorpg.Tools.DeploySmokeTest;

// The lines of a smoke test and its verdict: one line per check, and an exit code that is 0 only
// when every check held. A line never carries a secret: callers pass what happened, not what was
// sent.
public sealed class SmokeReport(TextWriter output)
{
    private readonly object sync = new();
    private int passed;
    private int failed;

    public int Passed => Volatile.Read(ref passed);

    public int Failed => Volatile.Read(ref failed);

    public int ExitCode => Failed == 0 && Passed > 0 ? 0 : 1;

    public void Pass(string check, string detail)
    {
        Interlocked.Increment(ref passed);
        Write("[ok]    ", check, detail);
    }

    public void Fail(string check, string detail)
    {
        Interlocked.Increment(ref failed);
        Write("[FAILED]", check, detail);
    }

    // Records one check that holds or not, and returns whether it held.
    public bool Check(string check, bool condition, string detail)
    {
        if (condition)
        {
            Pass(check, detail);
        }
        else
        {
            Fail(check, detail);
        }

        return condition;
    }

    // Runs one step of the scenario. An exception fails the step with its message and ends it, so
    // the steps that do not depend on it still run. Returns whether the step finished.
    public async Task<bool> StepAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(name, exception.Message);
            return false;
        }
    }

    public void Note(string text)
    {
        lock (sync)
        {
            output.WriteLine($"         {text}");
            output.Flush();
        }
    }

    private void Write(string state, string check, string detail)
    {
        lock (sync)
        {
            output.WriteLine($"{state} {check}: {detail}");
            output.Flush();
        }
    }
}
