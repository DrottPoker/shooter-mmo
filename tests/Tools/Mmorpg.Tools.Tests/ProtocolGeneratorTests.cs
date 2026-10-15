using System.Text.Json.Nodes;
using Mmorpg.Testing;
using Mmorpg.Tools.ProtocolGenerator;

namespace Mmorpg.Tools.Tests;

public sealed class ProtocolGeneratorTests
{
    [Fact]
    public void CommittedGeneratedProtocolMatchesTheSchema()
    {
        var result = ProtocolGeneration.Generate(RepositoryPaths.Root);
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files)
        {
            var path = ProtocolLayout.Resolve(RepositoryPaths.Root, file.RelativePath);
            expectedPaths.Add(path);
            Assert.True(File.Exists(path), $"'{file.RelativePath}' is missing. Run tools/ProtocolGenerator.");
            Assert.True(
                file.Content == File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal),
                $"'{file.RelativePath}' is stale. Run tools/ProtocolGenerator.");
        }

        var directory = ProtocolLayout.Resolve(RepositoryPaths.Root, ProtocolLayout.GeneratedDirectory);
        Assert.All(
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            path => Assert.Contains(Path.GetFullPath(path), expectedPaths));
    }

    [Fact]
    public void VerifyModeAcceptsTheCommittedFiles()
    {
        var result = ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(RepositoryPaths.Root, verifyOnly: true));

        Assert.Equal(14, result.Model.Version);
        Assert.Equal(27, result.Model.Messages.Count);
    }

    [Fact]
    public void VerifyRejectsStaleOrForeignFilesAndGenerationRepairsThem()
    {
        using var repository = new TemporaryRepository();
        ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: false));
        var verify = ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: true);
        ProtocolGeneration.Run(verify);
        var core = Path.Combine(repository.GeneratedDirectory, ProtocolEmitter.CoreFileName);

        File.AppendAllText(core, "// edited\n");
        Assert.Throws<InvalidDataException>(() => ProtocolGeneration.Run(verify));
        ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: false));
        ProtocolGeneration.Run(verify);

        var foreign = Path.Combine(repository.GeneratedDirectory, "Handwritten.cs");
        File.WriteAllText(foreign, "// not generated\n");
        Assert.Throws<InvalidDataException>(() => ProtocolGeneration.Run(verify));
        ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: false));
        Assert.False(File.Exists(foreign));
        ProtocolGeneration.Run(verify);
    }

    [Fact]
    public void GeneratedFilesKeepCrlfCheckoutsCurrent()
    {
        using var repository = new TemporaryRepository();
        ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: false));
        var core = Path.Combine(repository.GeneratedDirectory, ProtocolEmitter.CoreFileName);
        File.WriteAllText(core, File.ReadAllText(core).Replace("\n", "\r\n", StringComparison.Ordinal));

        ProtocolGeneration.Run(ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: true));
    }

    [Fact]
    public void AssetGuidsAreDerivedFromThePackagePath()
    {
        var core = ProtocolLayout.GeneratedDirectory + "/" + ProtocolEmitter.CoreFileName;

        Assert.Equal("ce8e1e36bd18ecbb600d78c42ec4ff56", ProtocolGeneration.AssetGuid(core));
        Assert.Equal(ProtocolGeneration.AssetGuid(core), ProtocolGeneration.AssetGuid(core));
        Assert.NotEqual(ProtocolGeneration.AssetGuid(core), ProtocolGeneration.AssetGuid(core + ".meta"));
        Assert.Matches("^[0-9a-f]{32}$", ProtocolGeneration.AssetGuid(ProtocolLayout.GeneratedDirectory));
    }

    [Fact]
    public void TheMinimalSchemaLoads()
    {
        var model = ProtocolSchemaLoader.Load(MinimalSchema().ToJsonString());

        Assert.Equal(3, model.Version);
        Assert.Single(model.Messages);
        Assert.Equal(MessageDelivery.Unreliable, model.Messages[0].Delivery);
    }

    [Theory]
    [InlineData("unknown root property", "$.unknown")]
    [InlineData("unknown message property", "$.messages[0].fields")]
    [InlineData("missing description", "$.description")]
    [InlineData("version zero", "$.protocol.version")]
    [InlineData("magic without hex prefix", "$.protocol.magic")]
    [InlineData("unreliable limit above packet limit", "$.protocol.maximumUnreliablePacketSize")]
    [InlineData("channel beyond count", "$.protocol.channels[0].id")]
    [InlineData("duplicate channel", "$.protocol.channels")]
    [InlineData("lowercase limit name", "$.limits[0].name")]
    [InlineData("unknown limit visibility", "$.limits[0].visibility")]
    [InlineData("flags value with two bits", "$.enums[0].values[1].value")]
    [InlineData("enum value above a byte", "$.enums[0].values[0].value")]
    [InlineData("duplicate enum value", "$.enums[0].values")]
    [InlineData("message id zero", "$.messages[0].id")]
    [InlineData("duplicate message id", "$.messages")]
    [InlineData("unknown channel", "$.messages[0].channel")]
    [InlineData("unknown delivery", "$.messages[0].delivery")]
    [InlineData("message named like an enum", "$")]
    public void InvalidSchemasAreRejectedWithTheirPath(string mutation, string path)
    {
        var schema = MinimalSchema();
        var protocol = schema["protocol"]!.AsObject();
        var enumDefinition = schema["enums"]![0]!.AsObject();
        var message = schema["messages"]![0]!.AsObject();
        switch (mutation)
        {
            case "unknown root property":
                schema["unknown"] = true;
                break;
            case "unknown message property":
                message["fields"] = new JsonArray();
                break;
            case "missing description":
                schema.Remove("description");
                break;
            case "version zero":
                protocol["version"] = 0;
                break;
            case "magic without hex prefix":
                protocol["magic"] = "4F4D4D53";
                break;
            case "unreliable limit above packet limit":
                protocol["maximumUnreliablePacketSize"] = 1201;
                break;
            case "channel beyond count":
                protocol["channels"]![0]!["id"] = 1;
                break;
            case "duplicate channel":
                protocol["channels"]!.AsArray().Add(protocol["channels"]![0]!.DeepClone());
                break;
            case "lowercase limit name":
                schema["limits"]![0]!["name"] = "maximumThings";
                break;
            case "unknown limit visibility":
                schema["limits"]![0]!["visibility"] = "internal";
                break;
            case "flags value with two bits":
                enumDefinition["values"]![1]!["value"] = 3;
                break;
            case "enum value above a byte":
                enumDefinition["values"]![0]!["value"] = 256;
                break;
            case "duplicate enum value":
                enumDefinition["values"]![1]!["value"] = 0;
                break;
            case "message id zero":
                message["id"] = 0;
                break;
            case "duplicate message id":
                var copy = message.DeepClone().AsObject();
                copy["name"] = "Other";
                schema["messages"]!.AsArray().Add(copy);
                break;
            case "unknown channel":
                message["channel"] = "Missing";
                break;
            case "unknown delivery":
                message["delivery"] = "eventually";
                break;
            case "message named like an enum":
                message["name"] = "Buttons";
                break;
        }

        var exception = Assert.Throws<ProtocolSchemaException>(() => ProtocolSchemaLoader.Load(schema.ToJsonString()));
        Assert.Equal(path, exception.Path);
    }

    [Fact]
    public void OptionsAcceptAnExplicitRootAndVerifyOnce()
    {
        Assert.True(ProtocolGeneratorOptions.TryParse(
            ["--repository-root", RepositoryPaths.Root, "--verify"],
            out var options,
            out _));
        Assert.True(options!.VerifyOnly);
        Assert.Equal(Path.GetFullPath(RepositoryPaths.Root), options.RepositoryRoot);
        Assert.False(ProtocolGeneratorOptions.TryParse(["--verify", "--verify"], out _, out _));
        Assert.False(ProtocolGeneratorOptions.TryParse(["--repository-root"], out _, out _));
        Assert.False(ProtocolGeneratorOptions.TryParse(["--unknown"], out _, out _));
    }

    private static JsonObject MinimalSchema()
    {
        return JsonNode.Parse(
            """
            {
              "description": "A test protocol.",
              "protocol": {
                "namespace": "Test.Protocol",
                "version": 3,
                "magic": "0x01020304",
                "connectionKey": "Test.Key",
                "maximumPacketSize": 1200,
                "maximumUnreliablePacketSize": 1000,
                "channelCount": 1,
                "channels": [ { "name": "ControlChannel", "id": 0, "description": "Control." } ]
              },
              "limits": [
                { "name": "MaximumThings", "value": 4, "visibility": "private", "description": "Things." }
              ],
              "enums": [
                {
                  "name": "Buttons",
                  "family": "Input",
                  "flags": true,
                  "description": "Buttons.",
                  "values": [ { "name": "None", "value": 0 }, { "name": "Jump", "value": 1 } ]
                }
              ],
              "messages": [
                {
                  "name": "Snapshot",
                  "id": 1,
                  "family": "Input",
                  "direction": "serverToClient",
                  "channel": "ControlChannel",
                  "delivery": "unreliable",
                  "codec": "handWritten",
                  "description": "A snapshot."
                }
              ]
            }
            """)!.AsObject();
    }

    // A throwaway repository that holds a copy of the real schema.
    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), "mmorpg-protocol-" + Guid.NewGuid().ToString("N"));
            var schema = ProtocolLayout.Resolve(Root, ProtocolLayout.SchemaPath);
            Directory.CreateDirectory(Path.GetDirectoryName(schema)!);
            File.Copy(ProtocolLayout.Resolve(RepositoryPaths.Root, ProtocolLayout.SchemaPath), schema);
            GeneratedDirectory = ProtocolLayout.Resolve(Root, ProtocolLayout.GeneratedDirectory);
        }

        public string Root { get; }

        public string GeneratedDirectory { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
