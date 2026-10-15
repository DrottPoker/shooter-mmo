using Mmorpg.Tools.ProtocolGenerator;

if (!ProtocolGeneratorOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: ProtocolGenerator " + ProtocolGeneratorOptions.Usage);
    return 1;
}

try
{
    var result = ProtocolGeneration.Run(options!);
    Console.WriteLine(
        "Realtime protocol version {0} {1}: {2} messages, {3} generated files.",
        result.Model.Version,
        options!.VerifyOnly ? "verified" : "generated",
        result.Model.Messages.Count,
        result.Files.Count);
    return 0;
}
catch (Exception exception) when (exception is ProtocolSchemaException or InvalidDataException)
{
    Console.Error.WriteLine("Realtime protocol generation failed: " + exception.Message);
    return 1;
}
