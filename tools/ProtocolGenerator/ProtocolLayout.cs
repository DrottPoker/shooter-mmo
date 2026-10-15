namespace Mmorpg.Tools.ProtocolGenerator;

// Repository paths of the protocol schema and the generated code.
public static class ProtocolLayout
{
    public const string SolutionFileName = "Mmorpg.slnx";

    // The Unity package that owns the protocol. Unity ignores folders that end in a tilde,
    // so the schema needs no .meta files and stays out of client builds.
    public const string PackageDirectory = "src/Shared/GameProtocol";

    public const string PackageName = "com.mmorpg.game-protocol";

    public const string SchemaPath = PackageDirectory + "/Schema~/realtime-protocol.json";

    // Generated C# below the package runtime folder. The generator owns this folder: it
    // writes every file in it and removes files it no longer produces.
    public const string GeneratedDirectory = PackageDirectory + "/Runtime/Generated";

    public static string Resolve(string repositoryRoot, string relativePath)
    {
        return Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
