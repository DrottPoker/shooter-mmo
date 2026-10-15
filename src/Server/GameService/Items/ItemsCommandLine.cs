namespace Mmorpg.GameService.Items;

// Development item commands that run instead of the web host. Both commands start
// after the database is initialized and before any hosted service.
public static class ItemsCommandLine
{
    // Returns the process exit code of an item command, or null when the arguments do
    // not name one.
    public static async Task<int?> TryRunAsync(
        IServiceProvider services,
        string[] args,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(logger);

        if (PhaseNineDevelopmentFixtureCommand.TryParse(
                args,
                out var phaseNineCharacterId,
                out var phaseNineFixtureError))
        {
            if (!string.IsNullOrWhiteSpace(phaseNineFixtureError))
            {
                throw new InvalidOperationException(phaseNineFixtureError);
            }

            await using var scope = services.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<PhaseNineDevelopmentFixtureSeeder>();
            var result = await seeder.SeedAsync(phaseNineCharacterId, CancellationToken.None);
            logger.LogInformation(
                "Seeded Phase 9 fixture for character {CharacterId} at revision {ItemStateRevision} with {GrantedItemCount} items, {RecoveryDeliveryCount} Recovery delivery, and carry {CarriedWeight}/{CarryCapacity}.",
                result.CharacterId,
                result.ItemStateRevision,
                result.GrantedItemCount,
                result.RecoveryDeliveryCount,
                result.CarriedWeight,
                result.CarryCapacity);
            return 0;
        }

        if (DevelopmentItemToolCommandParser.TryParse(
                args,
                out var developmentItemToolCommand,
                out var developmentItemToolError))
        {
            var exitCode = 0;
            DevelopmentItemToolResponse response;
            if (!string.IsNullOrWhiteSpace(developmentItemToolError)
                || developmentItemToolCommand is null)
            {
                response = new DevelopmentItemToolResponse(
                    false,
                    developmentItemToolError.Length == 0
                        ? "The development item tool command is invalid."
                        : developmentItemToolError,
                    null);
                exitCode = 1;
            }
            else
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<DevelopmentItemToolService>();
                    response = await service.ExecuteAsync(
                        developmentItemToolCommand,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    response = new DevelopmentItemToolResponse(false, exception.Message, null);
                    exitCode = 1;
                }
            }

            // The Unity tool reads this one line from standard output.
            Console.WriteLine(DevelopmentItemToolProtocol.Serialize(response));
            return exitCode;
        }

        return null;
    }
}
