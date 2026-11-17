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

    // The command, if the first argument names it, and the arguments that remain for the
    // configuration, so a configuration argument never takes a command word as its value.
    public static (GameServiceDatabaseCommand? Command, string[] ConfigurationArguments) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return (null, args);
        }

        if (string.Equals(args[0], AuditVerb, StringComparison.Ordinal))
        {
            return args.Length > 1 && string.Equals(args[1], JsonOption, StringComparison.Ordinal)
                ? (GameServiceDatabaseCommand.AuditJson, args[2..])
                : (GameServiceDatabaseCommand.Audit, args[1..]);
        }

        if (!string.Equals(args[0], MigrateVerb, StringComparison.Ordinal))
        {
            return (null, args);
        }

        return args.Length > 1 && string.Equals(args[1], PlanOption, StringComparison.Ordinal)
            ? (GameServiceDatabaseCommand.Plan, args[2..])
            : (GameServiceDatabaseCommand.Migrate, args[1..]);
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
                var plan = await initializer.PlanAsync(cancellationToken);
                await output.WriteAsync(plan.Describe("Pending migrations", "Pending seed changes"));
                await output.WriteLineAsync(plan.IsEmpty
                    ? "The database is current; migrate would change nothing."
                    : "Run migrate without --plan to apply them.");
                return 0;
            }

            var applied = await initializer.InitializeAsync(cancellationToken);
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
