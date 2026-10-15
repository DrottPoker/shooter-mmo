namespace Mmorpg.Tools.ProtocolGenerator;

// A schema that cannot generate a valid protocol. The message names the JSON path.
public sealed class ProtocolSchemaException : Exception
{
    public ProtocolSchemaException(string path, string message)
        : base($"{path}: {message}")
    {
        Path = path;
    }

    public string Path { get; }
}
