using Mmorpg.GameService.Platform.Audit;
using Npgsql;

namespace Mmorpg.GameService.Database;

public enum GameServiceDatabaseCommand
{
    Migrate,
    Plan,
    Audit,
    AuditJson
}

// `migrate` applies the pending migrations and seed changes and exits; `migrate --plan`
// prints them and applies nothing. `audit` runs the economy audit once and prints its report,
// `audit --json` as JSON. All of them run instead of the web host, before anything else
// touches the database.
public static class GameServiceDatabaseCommandLine
{
    public const string MigrateVerb = "migrate";
    public const string PlanOption = "--plan";
    public const string AuditVerb = "audit";
    public const string JsonOption = "--json";

    public const string MigrateAdvice =
        "Run the migrate command with the same configuration first: `Mmorpg.GameService migrate` "
        + "in the build output, or `dotnet run --project src/Server/GameService -- migrate` in a checkout. "
        + "`migrate --plan` lists what it would apply.";

    // A command verb, its one option, and the command that each form runs.
    private sealed record Verb(
        string Name,
        string Option,
        GameServiceDatabaseCommand Plain,
        GameServiceDatabaseCommand WithOption);

    private static readonly Verb[] Verbs =
    [
        new(MigrateVerb, PlanOption, GameServiceDatabaseCommand.Migrate, GameServiceDatabaseCommand.Plan),
        new(AuditVerb, JsonOption, GameServiceDatabaseCommand.Audit, GameServiceDatabaseCommand.AuditJson)
    ];

    // The command, if the first argument names it, and the arguments that remain for the
    // configuration, so a configuration argument never takes a command word as its value.
    //
    // After the verb, the command's own option (--plan, --json) counts at any position, written
    // exactly so, and is removed. Every other argument must be one that the configuration's command
    // line provider really reads, because that provider silently ignores what it does not
    // understand, and a read-only command must never fall through to a writing one: anything else
    // makes the command refuse before it connects to anything.
    public static (GameServiceDatabaseCommand? Command, string[] ConfigurationArguments) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return (null, args);
        }

        var verb = Array.Find(Verbs, candidate => string.Equals(candidate.Name, args[0], StringComparison.Ordinal));
        if (verb is null)
        {
            return (null, args);
        }

        var remaining = args[1..]
            .Where(argument => !string.Equals(argument, verb.Option, StringComparison.Ordinal))
            .ToArray();
        var reason = ConfigurationArgumentError(verb.Option, remaining);
        if (reason is not null)
        {
            throw new GameServiceDatabaseCommandLineException(
                $"{verb.Name} did not run: {reason} Usage: Mmorpg.GameService {verb.Name} [{verb.Option}] "
                + "[configuration arguments as --key=value, --key value, /key=value, /key value, or key=value]");
        }

        return (remaining.Length == args.Length - 1 ? verb.Plain : verb.WithOption, remaining);
    }

    // Why the arguments are not all configuration arguments that the command line provider reads
    // the way they are written, or null when they are. The provider takes --key=value, /key=value,
    // and key=value, and --key or /key with the next argument as the value. It ignores a bare word
    // and a --key without a following argument, and it takes whatever follows --key as the value.
    private static string? ConfigurationArgumentError(string option, IReadOnlyList<string> arguments)
    {
        var optionName = option[2..];
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var prefixLength = argument.StartsWith("--", StringComparison.Ordinal)
                ? 2
                : argument.StartsWith('/') || argument.StartsWith('-') ? 1 : 0;
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var key = argument[prefixLength..(separator < 0 ? argument.Length : separator)];

            // A near miss of the option would otherwise be read as a harmless configuration key.
            if (string.Equals(key, optionName, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{argument}' is not the option {option}, which is written exactly so, without a value.";
            }

            if (argument.StartsWith('-') && prefixLength == 1)
            {
                return $"'{argument}' starts with a single dash, which the configuration does not read.";
            }

            if (key.Length == 0)
            {
                return $"'{argument}' has an empty key.";
            }

            if (separator >= 0)
            {
                continue;
            }

            if (prefixLength == 0)
            {
                return $"'{argument}' is neither {option} nor a configuration argument.";
            }

            if (index + 1 >= arguments.Count)
            {
                return $"'{argument}' has no value, so it is neither {option} nor a configuration argument.";
            }

            var value = arguments[++index];
            if (LooksLikeAnArgument(value))
            {
                return $"'{argument}' would take '{value}' as its value, which looks like another argument; "
                    + $"write {argument}=<value>, and {option} exactly as written.";
            }
        }

        return null;
    }

    // What the provider would swallow as a value although it is most likely a switch.
    private static bool LooksLikeAnArgument(string value)
    {
        if (value.StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        var separator = value.IndexOf('=', StringComparison.Ordinal);
        return value.StartsWith('/') && separator > 1 && !value.AsSpan(1, separator - 1).Contains('/');
    }

    // Runs any command with the services of the built host.
    public static Task<int> RunAsync(
        GameServiceDatabaseCommand command,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var initializer = services.GetRequiredService<GameServiceDatabaseInitializer>();
        return command is GameServiceDatabaseCommand.Audit or GameServiceDatabaseCommand.AuditJson
            ? GameServiceAuditCommand.RunAsync(
                command == GameServiceDatabaseCommand.AuditJson,
                initializer,
                services.GetRequiredService<NpgsqlDataSource>(),
                services.GetServices<IAuditCheck>(),
                services.GetRequiredService<AuditOptions>(),
                output,
                error,
                cancellationToken)
            : RunAsync(command, initializer, output, error, cancellationToken);
    }

    // Runs migrate or migrate --plan. Returns the process exit code: 0 when the command
    // succeeded, 1 when it failed.
    public static async Task<int> RunAsync(
        GameServiceDatabaseCommand command,
        GameServiceDatabaseInitializer initializer,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initializer);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (command is not (GameServiceDatabaseCommand.Migrate or GameServiceDatabaseCommand.Plan))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Only migrate and its plan run with the initializer alone.");
        }

        try
        {
            if (command == GameServiceDatabaseCommand.Plan)
            {
                var plan = await initializer.PlanUnlessAheadAsync(cancellationToken);
                await output.WriteAsync(plan.Describe("Pending migrations", "Pending seed changes"));
                await output.WriteLineAsync(plan.IsEmpty
                    ? "The database is current; migrate would change nothing."
                    : "Run migrate without --plan to apply them.");
                return 0;
            }

            var applied = await initializer.MigrateAsync(cancellationToken);
            await output.WriteAsync(applied.Describe("Applied migrations", "Seed changes"));
            await output.WriteLineAsync("The database is current.");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync(
                (command == GameServiceDatabaseCommand.Plan ? "migrate --plan failed: " : "migrate failed: ")
                + Describe(exception));
            return 1;
        }
    }

    internal static string Describe(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(Environment.NewLine + "  caused by: ", messages.Distinct(StringComparer.Ordinal));
    }
}

// A command line that names a database command but that the command refuses before it connects to
// anything. The message is one line that names the argument and the usage.
public sealed class GameServiceDatabaseCommandLineException(string message) : Exception(message);
