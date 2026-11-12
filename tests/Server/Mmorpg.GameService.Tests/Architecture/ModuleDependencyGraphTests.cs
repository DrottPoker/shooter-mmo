using Mmorpg.GameService.Testing;

namespace Mmorpg.GameService.Tests.Architecture;

// The project references enforce Platform <- Accounts <- Characters <- Placement <- Items
// <- Quests <- Economy <- Content <- host. This pins the compiled graph, so a new edge is a
// deliberate decision.
public sealed class ModuleDependencyGraphTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Mmorpg.GameService.Platform"] = [],
            ["Mmorpg.GameService.Accounts"] = ["Mmorpg.GameService.Platform"],
            ["Mmorpg.GameService.Characters"] =
                ["Mmorpg.GameService.Accounts", "Mmorpg.GameService.Platform"],
            ["Mmorpg.GameService.Placement"] =
                ["Mmorpg.GameService.Accounts", "Mmorpg.GameService.Characters", "Mmorpg.GameService.Platform"],
            ["Mmorpg.GameService.Items"] =
            [
                "Mmorpg.GameService.Accounts",
                "Mmorpg.GameService.Characters",
                "Mmorpg.GameService.Placement",
                "Mmorpg.GameService.Platform"
            ],
            // Quests runs its commands in Items' item transaction kernel and credits quest
            // rewards through Characters' wallet.
            ["Mmorpg.GameService.Quests"] =
            [
                "Mmorpg.GameService.Accounts",
                "Mmorpg.GameService.Characters",
                "Mmorpg.GameService.Items",
                "Mmorpg.GameService.Placement",
                "Mmorpg.GameService.Platform"
            ],
            // Economy runs its commands in Items' item transaction kernel and on Items' worker item
            // route, moves currency through Characters' wallet, and credits gathering through
            // Quests.
            ["Mmorpg.GameService.Economy"] =
            [
                "Mmorpg.GameService.Characters",
                "Mmorpg.GameService.Items",
                "Mmorpg.GameService.Platform",
                "Mmorpg.GameService.Quests"
            ],
            // Content compares its catalogs through Placement's revision contracts.
            ["Mmorpg.GameService.Content"] =
                ["Mmorpg.GameService.Placement", "Mmorpg.GameService.Platform"],
            ["Mmorpg.GameService"] =
            [
                "Mmorpg.GameService.Accounts",
                "Mmorpg.GameService.Characters",
                "Mmorpg.GameService.Content",
                "Mmorpg.GameService.Economy",
                "Mmorpg.GameService.Items",
                "Mmorpg.GameService.Placement",
                "Mmorpg.GameService.Platform",
                "Mmorpg.GameService.Quests"
            ]
        };

    [Fact]
    public void ModulesReferenceOnlyLowerModules()
    {
        foreach (var assembly in GameServiceAssemblies.All)
        {
            var name = assembly.GetName().Name!;
            var references = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(reference => reference.StartsWith("Mmorpg.GameService", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(
                ExpectedReferences.TryGetValue(name, out var expected),
                $"{name} is not a known GameService assembly.");
            Assert.Equal(expected, references);
        }
    }

    [Fact]
    public void HostKeepsItsAssemblyAndProcessName()
    {
        // Tools attach to the GameService process by this name.
        Assert.Equal("Mmorpg.GameService", GameServiceAssemblies.Host.GetName().Name);
    }
}
