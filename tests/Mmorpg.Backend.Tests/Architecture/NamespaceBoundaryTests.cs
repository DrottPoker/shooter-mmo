using System.Text.RegularExpressions;
using Mmorpg.GameService.Testing;

namespace Mmorpg.Backend.Tests.Architecture;

// A module reaches another module only through that module's Contracts namespace and
// only down the order Platform, Accounts, Characters, Placement, Items, host. The
// compiler enforces the order; this also keeps modules off each other's module classes
// and options. The host composes the modules and may reference all of them.
public sealed partial class NamespaceBoundaryTests
{
    // Namespaces mapped to the module that owns them.
    private static readonly (string Namespace, string Module)[] NamespaceRules =
    [
        ("Mmorpg.GameService.Platform", GameServiceSourceMap.Platform),
        ("Mmorpg.GameService.Accounts", GameServiceSourceMap.Accounts),
        ("Mmorpg.GameService.Characters", GameServiceSourceMap.Characters),
        ("Mmorpg.GameService.Placement", GameServiceSourceMap.Placement),
        ("Mmorpg.GameService.Items", GameServiceSourceMap.Items),
        ("Mmorpg.GameService.Config", GameServiceSourceMap.Host),
        ("Mmorpg.GameService.Database", GameServiceSourceMap.Host),
        ("Mmorpg.GameService.Health", GameServiceSourceMap.Host)
    ];

    [Fact]
    public void ModulesReferenceOtherModulesOnlyThroughLowerContracts()
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in GameServiceSourceMap.LoadSourceFiles())
        {
            if (string.Equals(file.Module, GameServiceSourceMap.Host, StringComparison.Ordinal))
            {
                continue;
            }

            var sourceRank = GameServiceSourceMap.RankOf(file.Module);
            // A file's own namespace declaration is not a reference.
            var text = NamespaceDeclarationPattern().Replace(file.Text, string.Empty);
            foreach (Match match in NamespaceReferencePattern().Matches(text))
            {
                var referencedNamespace = match.Groups["namespace"].Value;
                var referencedModule = ModuleOfNamespace(referencedNamespace);
                if (referencedModule is null
                    || string.Equals(referencedModule, file.Module, StringComparison.Ordinal))
                {
                    continue;
                }

                var isLower = GameServiceSourceMap.RankOf(referencedModule) < sourceRank;
                var isPlatform = string.Equals(
                    referencedModule,
                    GameServiceSourceMap.Platform,
                    StringComparison.Ordinal);
                var isContract = referencedNamespace.StartsWith(
                    $"Mmorpg.GameService.{referencedModule}.Contracts",
                    StringComparison.Ordinal);
                if (!isLower || (!isPlatform && !isContract))
                {
                    violations.Add($"{file.RelativePath} -> {referencedNamespace}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "A module may only reference lower modules' Contracts namespaces and Platform:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string? ModuleOfNamespace(string value)
    {
        foreach (var (prefix, module) in NamespaceRules)
        {
            if (string.Equals(value, prefix, StringComparison.Ordinal)
                || value.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                return module;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\b(?<namespace>Mmorpg\.GameService(?:\.[A-Z][A-Za-z]*)+)")]
    private static partial Regex NamespaceReferencePattern();

    [GeneratedRegex(@"^namespace\s+[A-Za-z.]+;", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclarationPattern();
}
