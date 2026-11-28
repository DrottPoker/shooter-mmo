using System.Globalization;
using Mmorpg.GameService.Characters.Contracts;
using Mmorpg.GameService.Items.Contracts;
using Mmorpg.GameService.Items.Persistence;
using Mmorpg.GameService.Platform.Database;
using Mmorpg.WorldData.Items;
using Npgsql;

namespace Mmorpg.GameService.Items;

// The current item catalog is mirrored first, because the carry recompute needs it. Content
// that names items of this catalog checks it first, so a catalog that would break that content
// stops the start before anything is mirrored. The recompute runs only for a catalog revision
// that it has not completed for, so a start with unchanged content reads no character. A
// definition that requires a level above the highest character level could never be used, so
// it stops the start too.
internal sealed class ItemsDatabaseSeeder(
    NpgsqlDataSource dataSource,
    ItemCatalogSeeder itemCatalogSeeder,
    CharacterItemStateBootstrapper characterItemStateBootstrapper,
    ItemCatalogSource catalogSource,
    IItemCatalogReferences catalogReferences,
    ICharacterProgression characterProgression) : IDatabaseSeeder
{
    private const int ReportedDefinitions = 5;

    public async Task<IReadOnlyList<string>> PlanAsync(CancellationToken cancellationToken)
    {
        // The canonical form verifies the revision, as a seed does.
        var catalog = ItemCatalogRuntimeLoader.ValidateAndCanonicalize(await LoadAsync(cancellationToken));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var mirrored = await ItemCatalogSeeder.ReadCurrentRevisionAsync(
            connection,
            catalog.CatalogId,
            cancellationToken);
        var recomputed = await PostgresCatalog.TableExistsAsync(connection, ItemDataSteps.Table, cancellationToken)
            ? await ItemDataSteps.ReadAsync(connection, ItemDataSteps.CarryStateRecompute, cancellationToken)
            : null;

        var changes = new List<string>();
        if (!string.Equals(mirrored, catalog.Revision, StringComparison.Ordinal))
        {
            changes.Add(
                $"Item catalog {catalog.CatalogId}: mirror revision {catalog.Revision}; the database holds {mirrored ?? "none"}.");
        }

        if (!string.Equals(recomputed, catalog.Revision, StringComparison.Ordinal))
        {
            changes.Add(
                $"Carry state: recompute every character for item catalog revision {catalog.Revision}; "
                + $"last computed for {recomputed ?? "none"}.");
        }

        return changes;
    }

    public async Task<IReadOnlyList<string>> SeedAsync(CancellationToken cancellationToken)
    {
        var catalog = await LoadAsync(cancellationToken);
        var mirror = await itemCatalogSeeder.SeedAsync(catalog, cancellationToken);
        var changes = new List<string>();
        if (mirror.RevisionChanged)
        {
            changes.Add(
                $"Item catalog {mirror.CatalogId}: mirrored revision {mirror.Revision}; was {mirror.PreviousRevision ?? "none"}.");
        }

        var changed = await characterItemStateBootstrapper.RecomputeCarryStateAsync(mirror.Revision, cancellationToken);
        if (changed is { } count)
        {
            changes.Add(
                $"Carry state: recomputed every character for item catalog revision {mirror.Revision}; {count} changed.");
        }

        return changes;
    }

    private async Task<ItemCatalogRuntimeDocument> LoadAsync(CancellationToken cancellationToken)
    {
        var catalog = await ItemCatalogRuntimeLoader.LoadAsync(catalogSource.RuntimeCatalogPath, cancellationToken);
        catalogReferences.Validate(catalog);
        EnsureLevelsWithinMaximum(catalog, characterProgression.MaximumLevel);
        return catalog;
    }

    private static void EnsureLevelsWithinMaximum(ItemCatalogRuntimeDocument catalog, int maximumLevel)
    {
        var aboveMaximum = (catalog.Definitions ?? [])
            .Where(definition => definition.RequiredCharacterLevel > maximumLevel)
            .ToArray();
        if (aboveMaximum.Length == 0)
        {
            return;
        }

        throw new ItemCatalogCompatibilityException(
            $"Item catalog {catalog.CatalogId} requires a character level above the highest level "
            + $"{maximumLevel.ToString(CultureInfo.InvariantCulture)} of the progression rules for "
            + $"{aboveMaximum.Length.ToString(CultureInfo.InvariantCulture)} item definitions: "
            + string.Join(
                "; ",
                aboveMaximum.Take(ReportedDefinitions).Select(definition =>
                    $"{definition.Id} requires level {definition.RequiredCharacterLevel.ToString(CultureInfo.InvariantCulture)}"))
            + ". Lower the requirement or raise maximumCharacterLevel.");
    }
}
