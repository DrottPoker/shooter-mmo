using Mmorpg.Tools.ContentPipeline;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Text;

if (!ContentToolOptions.TryParse(args, out var options, out var argumentError))
{
    Console.Error.WriteLine(argumentError);
    Console.Error.WriteLine("Usage: TextTableCompiler " + ContentToolOptions.Usage);
    return 1;
}

try
{
    // Every table that is written by hand has exactly one source file.
    foreach (var tableId in TextSource.TableIds)
    {
        var source = StrictContentJson.DeserializeFile<TextSourceDocument>(
            ContentLayout.TextSourcePath(options!.ContentRoot, tableId),
            $"Text source '{tableId}'");
        if (!string.Equals(source.TableId, tableId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Text source '{tableId}' names the table '{source.TableId}'.");
        }

        TextTableOutput.WriteOrVerify(
            options.GeneratedRoot,
            TextSource.Compile(source),
            options.VerifyOnly);
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Text table compilation failed: " + exception.Message);
    return 1;
}
