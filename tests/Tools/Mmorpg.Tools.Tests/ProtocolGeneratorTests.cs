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

        Assert.Equal(17, result.Model.Version);
        Assert.Equal(28, result.Model.Messages.Count);
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
    public void TheWireFingerprintIgnoresNamesAndDescriptionsButNotTheContract()
    {
        var fingerprint = ProtocolFingerprint.Compute(ProtocolSchemaLoader.Load(GeneratedSchema().ToJsonString()));

        var renamed = GeneratedSchema();
        renamed["messages"]![0]!["fields"]![2]!["name"] = "Pressed";
        renamed["messages"]![0]!["description"] = "Another description.";
        renamed["structs"]![0]!["fields"]![0]!["name"] = "Left";
        Assert.Equal(fingerprint, ProtocolFingerprint.Compute(ProtocolSchemaLoader.Load(renamed.ToJsonString())));

        var changedRule = GeneratedSchema();
        changedRule["messages"]![0]!["fields"]![0]!["minimum"] = 2;
        Assert.NotEqual(fingerprint, ProtocolFingerprint.Compute(ProtocolSchemaLoader.Load(changedRule.ToJsonString())));

        var changedLimit = GeneratedSchema();
        changedLimit["limits"]![0]!["value"] = 3;
        Assert.NotEqual(fingerprint, ProtocolFingerprint.Compute(ProtocolSchemaLoader.Load(changedLimit.ToJsonString())));

        var reordered = GeneratedSchema();
        var fields = reordered["messages"]![0]!["fields"]!.AsArray();
        var held = fields[2]!;
        fields.RemoveAt(2);
        fields.Insert(0, held);
        Assert.NotEqual(fingerprint, ProtocolFingerprint.Compute(ProtocolSchemaLoader.Load(reordered.ToJsonString())));
    }

    [Fact]
    public void TheVersionLockRejectsAChangedContractUntilTheVersionGrows()
    {
        using var repository = new TemporaryRepository();
        var generate = ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: false);
        var verify = ProtocolGeneratorOptions.Create(repository.Root, verifyOnly: true);
        var current = ProtocolGeneration.Run(generate).Model.Version;
        var lockPath = ProtocolLayout.Resolve(repository.Root, ProtocolLayout.LockPath);
        var lockedVersions = ProtocolVersionLock.Parse(File.ReadAllText(lockPath)).Keys.ToArray();
        Assert.Equal(current, lockedVersions[^1]);

        repository.EditSchema(schema => schema["protocol"]!["connectionKey"] = "Mmorpg.Realtime.v9");
        var changed = Assert.Throws<InvalidDataException>(() => ProtocolGeneration.Run(generate));
        Assert.Contains("Increase protocol.version", changed.Message, StringComparison.Ordinal);

        repository.EditSchema(schema => schema["protocol"]!["version"] = current + 1);
        Assert.Throws<InvalidDataException>(() => ProtocolGeneration.Run(verify));
        ProtocolGeneration.Run(generate);
        ProtocolGeneration.Run(verify);
        Assert.Equal(
            lockedVersions.Append(current + 1),
            ProtocolVersionLock.Parse(File.ReadAllText(lockPath)).Keys);

        repository.EditSchema(schema => schema["protocol"]!["version"] = 13);
        var lower = Assert.Throws<InvalidDataException>(() => ProtocolGeneration.Run(generate));
        Assert.Contains("versions only grow", lower.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "versions": [ { "version": 14 } ] }""")]
    [InlineData("""{ "versions": [ { "version": 0, "fingerprint": "00" } ] }""")]
    [InlineData("""{ "versions": [ { "version": 14, "fingerprint": "ABCDEF" } ] }""")]
    [InlineData("""{ "versions": [], "unknown": true }""")]
    [InlineData("not json")]
    public void InvalidVersionLocksAreRejected(string content)
    {
        Assert.Throws<InvalidDataException>(() => ProtocolVersionLock.Update(content, 14, new string('0', 64)));
    }

    [Fact]
    public void TheVersionLockRejectsADuplicateVersion()
    {
        var entry = $$"""{ "version": 14, "fingerprint": "{{new string('a', 64)}}" }""";
        var content = $$"""{ "versions": [ {{entry}}, {{entry}} ] }""";

        Assert.Throws<InvalidDataException>(() => ProtocolVersionLock.Update(content, 14, new string('a', 64)));
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
    [InlineData("unknown message property", "$.messages[0].codec")]
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
                message["codec"] = "generated";
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
    public void GeneratedMessagesResolveTheirTypesAndSizes()
    {
        var model = ProtocolSchemaLoader.Load(GeneratedSchema().ToJsonString());

        var message = Assert.Single(model.Messages);
        Assert.Equal("snapshot", message.Parameter);
        var points = Assert.IsType<ArrayWireType>(message.Fields[1].Type);
        Assert.Same(model.Structs.Single(), Assert.IsType<StructWireType>(points.Element.Type).Definition);
        Assert.Equal(new SizeRange(7 + 4 + 1 + 9 + 1, 7 + 4 + 1 + (4 * 9) + 1), WireSizes.OfMessage(message));
        Assert.Equal(7 + 4 + 1 + 1, WireSizes.EmptyMessageSize(message));
        Assert.Equal(SizeRange.Fixed(9), WireSizes.OfFields(model.Structs.Single().Fields));
    }

    [Fact]
    public void GeneratedFamiliesContainModelsCodecsAndSizeConstants()
    {
        var model = ProtocolSchemaLoader.Load(GeneratedSchema().ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Contains("public sealed partial class RealtimePoint", family, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class RealtimeSnapshot", family, StringComparison.Ordinal);
        Assert.Contains("public const int PointSize = 9;", family, StringComparison.Ordinal);
        Assert.Contains("public const int EmptySnapshotSize = 13;", family, StringComparison.Ordinal);
        Assert.Contains("public static byte[] EncodeSnapshot(RealtimeSnapshot snapshot)", family, StringComparison.Ordinal);
        Assert.Contains("var writer = StartPacket(RealtimeMessageType.Snapshot, SizeOfSnapshot(snapshot));", family, StringComparison.Ordinal);
        Assert.Contains("size += 1 + (value.Points.Length * 9);", family, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeOfPoint", family, StringComparison.Ordinal);
        Assert.Contains("!TryStartPacket(data, RealtimeMessageType.Snapshot, out var reader, out error)", family, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO", family, StringComparison.Ordinal);
        Assert.Contains("|| value.Sequence < 1", family, StringComparison.Ordinal);
        Assert.Contains("|| value.Points.Length > MaximumThings", family, StringComparison.Ordinal);
        Assert.Contains("return SatisfiesRules(value);", family, StringComparison.Ordinal);
        Assert.Contains("if ((flags & ~0x01) != 0)", family, StringComparison.Ordinal);
        Assert.Contains("private static bool IsDefined(RealtimeButtons value)", family, StringComparison.Ordinal);
    }

    [Fact]
    public void TextFieldsReadWriteAndValidateTheirLimits()
    {
        var schema = GeneratedSchema();
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Owner", "type": "string", "maxLength": "MaximumThings", "format": "guid" }"""));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Section", "type": "string", "maxLength": 8, "oneOf": [ "bag", "equipment" ] }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(
            new SizeRange(7 + 4 + 1 + 9 + 1 + 3 + 3, 7 + 4 + 1 + (4 * 9) + 1 + 2 + (3 * 4) + 2 + (3 * 8)),
            WireSizes.OfMessage(model.Messages.Single()));
        Assert.Contains("reader.TryReadString(MaximumThings, out var owner, out error)", family, StringComparison.Ordinal);
        Assert.Contains("writer.WriteString(value.Owner);", family, StringComparison.Ordinal);
        Assert.Contains("size += StringSize(value.Owner);", family, StringComparison.Ordinal);
        Assert.Contains("|| string.IsNullOrWhiteSpace(value.Owner)", family, StringComparison.Ordinal);
        Assert.Contains("|| !Guid.TryParse(value.Owner, out _)", family, StringComparison.Ordinal);
        Assert.Contains("|| (value.Section != \"bag\" && value.Section != \"equipment\")", family, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalFieldsWriteTheirOwnPresenceFlag()
    {
        var schema = GeneratedSchema();
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Label", "type": "string", "maxLength": 8, "optional": true, "nullAsEmpty": true }"""));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Origin", "type": "Point", "optional": true }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(
            new SizeRange(7 + 4 + 1 + 9 + 1 + 1 + 1, 7 + 4 + 1 + (4 * 9) + 1 + 1 + 2 + (3 * 8) + 1 + 9),
            WireSizes.OfMessage(model.Messages.Single()));
        Assert.Contains("Label = label ?? string.Empty;", family, StringComparison.Ordinal);
        Assert.Contains("var hasLabel = !string.IsNullOrWhiteSpace(value.Label);", family, StringComparison.Ordinal);
        Assert.Contains("writer.WriteBoolean(hasLabel);", family, StringComparison.Ordinal);
        Assert.Contains("var hasOrigin = value.Origin != null;", family, StringComparison.Ordinal);
        Assert.Contains("string label = string.Empty;", family, StringComparison.Ordinal);
        Assert.Contains("RealtimePoint origin = null;", family, StringComparison.Ordinal);
        Assert.Contains("reader.TryReadString(8, out label, out error)", family, StringComparison.Ordinal);
        Assert.Contains("|| value.Label == null", family, StringComparison.Ordinal);
        Assert.DoesNotContain("|| string.IsNullOrWhiteSpace(value.Label)", family, StringComparison.Ordinal);
        Assert.Contains("|| (value.Origin != null && !IsValidPoint(value.Origin))", family, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentGroupsRequireAbsentFieldsAndVariantStructsCountAsUsed()
    {
        var schema = UnionSchema();
        schema["structs"] = GeneratedSchema()["structs"]!.DeepClone();
        var variants = schema["messages"]![0]!["variants"]!;
        variants["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Weight", "type": "i64" }"""));
        variants["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Origin", "type": "Point" }"""));
        variants["cases"]![0]!["fields"]!.AsArray().Insert(0, JsonNode.Parse("""{ "field": "Origin" }"""));
        variants["cases"]![1]!["fields"]![0]!["group"]!.AsArray().Add(JsonNode.Parse("""{ "field": "Weight", "minimum": 0 }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Contains("|| (value.Target == Guid.Empty && value.Weight != 0)", family, StringComparison.Ordinal);
        Assert.Contains("private static bool IsValidPoint(RealtimePoint value)", family, StringComparison.Ordinal);
    }

    [Fact]
    public void VariantsGenerateFactoriesAndPerCaseCodecs()
    {
        var model = ProtocolSchemaLoader.Load(UnionSchema().ToJsonString());
        var message = model.Messages.Single();
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(new SizeRange(7 + 16 + 1 + 1, 7 + 16 + 1 + 1 + 16), WireSizes.OfMessage(message));
        Assert.Contains("internal RealtimeSnapshot(", family, StringComparison.Ordinal);
        Assert.Contains("public static RealtimeSnapshot CreateCount(", family, StringComparison.Ordinal);
        Assert.Contains("int amount = -1)", family, StringComparison.Ordinal);
        Assert.Contains("RealtimeSlot.Count,", family, StringComparison.Ordinal);
        Assert.Contains("case RealtimeSlot.Target:", family, StringComparison.Ordinal);
        Assert.Contains("var hasTarget = value.Target != Guid.Empty;", family, StringComparison.Ordinal);
        Assert.Contains("error = \"Snapshot kind is invalid.\";", family, StringComparison.Ordinal);
        Assert.Contains("if (value.Amount < 0)", family, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalFieldsAreWrittenReadAndValidatedByTheirCondition()
    {
        var schema = GeneratedSchema();
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Failed", "type": "bool" }"""));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Origin", "type": "Point", "when": "Failed" }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(7 + 4 + 1 + 9 + 1 + 1, WireSizes.OfMessage(model.Messages.Single()).Minimum);
        Assert.Contains("RealtimePoint origin = null;", family, StringComparison.Ordinal);
        Assert.Contains("if (failed)", family, StringComparison.Ordinal);
        Assert.Contains("|| value.Failed != (value.Origin != null)", family, StringComparison.Ordinal);
        Assert.Contains("|| (value.Origin != null && !IsValidPoint(value.Origin))", family, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptOnDecodeRelaxesTextOnlyWhileDecoding()
    {
        var schema = GeneratedSchema();
        schema["structs"]!.AsArray().Add(JsonNode.Parse(
            """
            {
              "name": "Label",
              "family": "Input",
              "description": "A label.",
              "fields": [ { "name": "Text", "type": "string", "maxLength": 8 } ]
            }
            """));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Note", "type": "string", "maxLength": 8, "blank": "acceptOnDecode" }"""));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Reason", "type": "Label", "blank": "acceptOnDecode" }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.True(model.Messages.Single().Fields[3].AcceptBlankOnDecode);
        Assert.Contains("private static bool IsValidSnapshot(RealtimeSnapshot value, bool decoding = false)", family, StringComparison.Ordinal);
        Assert.Contains("if (!IsValidSnapshot(decoded, decoding: true))", family, StringComparison.Ordinal);
        Assert.Contains("|| (!decoding && string.IsNullOrWhiteSpace(value.Note))", family, StringComparison.Ordinal);
        Assert.Contains("|| !IsValidLabel(value.Reason, acceptBlankText: decoding)", family, StringComparison.Ordinal);
        Assert.Contains("TryReadLabel(ref reader, out var reason, out error)", family, StringComparison.Ordinal);
        Assert.Contains("private static bool IsValidLabel(RealtimeLabel value, bool acceptBlankText = false)", family, StringComparison.Ordinal);
        Assert.DoesNotContain("IsValidLabel(decoded", family, StringComparison.Ordinal);
        Assert.Contains("|| (!acceptBlankText && string.IsNullOrWhiteSpace(value.Text))", family, StringComparison.Ordinal);
        Assert.Contains("private static bool IsValidPoint(RealtimePoint value)", family, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankTextAndNullAsEmptyFieldsAllowEmptyValues()
    {
        var schema = GeneratedSchema();
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Note", "type": "string", "maxLength": 8, "blank": "allow", "nullAsEmpty": true }"""));
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(
            """{ "name": "Pressed", "type": "array", "items": "Buttons", "maxCount": 2, "nullAsEmpty": true }"""));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(7 + 4 + 1 + 9 + 1 + 2 + 1, WireSizes.OfMessage(model.Messages.Single()).Minimum);
        Assert.Contains("Note = note ?? string.Empty;", family, StringComparison.Ordinal);
        Assert.Contains("Pressed = pressed ?? Array.Empty<RealtimeButtons>();", family, StringComparison.Ordinal);
        Assert.Contains("Points = points;", family, StringComparison.Ordinal);
        Assert.Contains("writer.WriteString(value.Note);", family, StringComparison.Ordinal);
        Assert.Contains("reader.TryReadString(8, out var note, out error, allowBlank: true)", family, StringComparison.Ordinal);
        Assert.Contains("|| value.Note == null", family, StringComparison.Ordinal);
        Assert.DoesNotContain("string.IsNullOrWhiteSpace(value.Note)", family, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAndExpandedMessagesTakeTheirOwnEncodeParameters()
    {
        var schema = MinimalSchema();
        schema["structs"] = JsonNode.Parse(
            """
            [
              {
                "name": "Label",
                "family": "Input",
                "description": "A label.",
                "fields": [
                  { "name": "Code", "type": "string", "maxLength": 8 },
                  { "name": "Text", "type": "string", "maxLength": 8 }
                ]
              }
            ]
            """);
        var snapshot = schema["messages"]![0]!.AsObject();
        snapshot["api"] = "empty";
        snapshot.Remove("fields");
        schema["messages"]!.AsArray().Add(JsonNode.Parse(
            """
            {
              "name": "Rejected",
              "id": 2,
              "family": "Input",
              "direction": "serverToClient",
              "channel": "ControlChannel",
              "delivery": "reliableOrdered",
              "description": "A rejection.",
              "api": "unwrap",
              "encodeParameters": "expanded",
              "fields": [ { "name": "Reason", "type": "Label" } ]
            }
            """));
        var model = ProtocolSchemaLoader.Load(schema.ToJsonString());
        var family = ProtocolEmitter.Emit(model).Single(file => file.FileName == "RealtimeProtocol.Input.g.cs").Content;

        Assert.Equal(SizeRange.Fixed(7), WireSizes.OfMessage(model.Messages[0]));
        Assert.Contains("public static byte[] EncodeSnapshot()", family, StringComparison.Ordinal);
        Assert.Contains("var writer = StartPacket(RealtimeMessageType.Snapshot, 0);", family, StringComparison.Ordinal);
        Assert.Contains("public static bool TryDecodeSnapshot(byte[] data, out string error)", family, StringComparison.Ordinal);
        Assert.DoesNotContain("class RealtimeSnapshot", family, StringComparison.Ordinal);
        Assert.Contains("public static byte[] EncodeRejected(string code, string text)", family, StringComparison.Ordinal);
        Assert.Contains("var reason = new RealtimeLabel(code, text);", family, StringComparison.Ordinal);
        Assert.Contains("public static bool TryDecodeRejected(byte[] data, out RealtimeLabel reason, out string error)", family, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("discriminator that is not an enum", "$.messages[0].variants.discriminator")]
    [InlineData("kind without a case", "$.messages[0].variants.cases")]
    [InlineData("factory used twice", "$.messages[0].variants.cases[1].kinds[0].factory")]
    [InlineData("rule on a variant field", "$.messages[0].variants.fields[0]")]
    [InlineData("variant field in no case", "$.messages[0].variants.fields")]
    [InlineData("fixed guid that is not empty", "$.messages[0].variants.cases[1].fields[0].group[0].fixed")]
    [InlineData("default before a required parameter", "$.messages[0].variants.cases[0].fields")]
    [InlineData("condition on a non-struct field", "$.messages[0].fields[2].when")]
    [InlineData("condition on a missing bool", "$.messages[0].fields")]
    [InlineData("acceptOnDecode on a variant field", "$.messages[0].variants.fields[2].blank")]
    public void InvalidVariantsAndConditionsAreRejectedWithTheirPath(string mutation, string path)
    {
        var schema = UnionSchema();
        var message = schema["messages"]![0]!.AsObject();
        var variants = message["variants"]!.AsObject();
        var cases = variants["cases"]!.AsArray();
        switch (mutation)
        {
            case "discriminator that is not an enum":
                variants["discriminator"] = "Id";
                break;
            case "kind without a case":
                cases.RemoveAt(1);
                variants["fields"]!.AsArray().RemoveAt(1);
                break;
            case "factory used twice":
                cases[1]!["kinds"]![0]!["factory"] = "CreateCount";
                break;
            case "rule on a variant field":
                variants["fields"]![0]!["minimum"] = 0;
                break;
            case "variant field in no case":
                variants["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Unused", "type": "u8" }"""));
                break;
            case "fixed guid that is not empty":
                cases[1]!["fields"]![0]!["group"]![0]!["fixed"] = "zero";
                break;
            case "default before a required parameter":
                cases[0]!["fields"]!.AsArray().Add(JsonNode.Parse("""{ "field": "Target" }"""));
                break;
            case "condition on a non-struct field":
                message["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Note", "type": "u8", "when": "Id" }"""));
                break;
            case "condition on a missing bool":
                message["fields"]!.AsArray().Add(JsonNode.Parse("""{ "name": "Note", "type": "Point", "when": "!Missing" }"""));
                schema["structs"] = GeneratedSchema()["structs"]!.DeepClone();
                break;
            case "acceptOnDecode on a variant field":
                variants["fields"]!.AsArray().Add(JsonNode.Parse(
                    """{ "name": "Note", "type": "string", "maxLength": 4, "blank": "acceptOnDecode" }"""));
                break;
        }

        var exception = Assert.Throws<ProtocolSchemaException>(() => ProtocolSchemaLoader.Load(schema.ToJsonString()));
        Assert.Equal(path, exception.Path);
    }

    [Theory]
    [InlineData("string without a maximum length", "$.messages[0].fields[3].maxLength")]
    [InlineData("string array without an items object", "$.messages[0].fields[3].items")]
    [InlineData("empty oneOf", "$.messages[0].fields[3].oneOf")]
    [InlineData("unknown string format", "$.messages[0].fields[3].format")]
    public void InvalidTextFieldsAreRejectedWithTheirPath(string mutation, string path)
    {
        var schema = GeneratedSchema();
        var field = mutation switch
        {
            "string without a maximum length" => """{ "name": "Label", "type": "string" }""",
            "string array without an items object" => """{ "name": "Tags", "type": "array", "items": "string", "maxCount": 2 }""",
            "empty oneOf" => """{ "name": "Label", "type": "string", "maxLength": 4, "oneOf": [] }""",
            _ => """{ "name": "Label", "type": "string", "maxLength": 4, "format": "email" }"""
        };
        schema["messages"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(field));

        var exception = Assert.Throws<ProtocolSchemaException>(() => ProtocolSchemaLoader.Load(schema.ToJsonString()));
        Assert.Equal(path, exception.Path);
    }

    [Theory]
    [InlineData("unknown field type", "$.messages[0].fields[0].type")]
    [InlineData("rule that does not fit the type", "$.messages[0].fields[0].nonEmpty")]
    [InlineData("bound outside the field type", "$.messages[0].fields[0].maximum")]
    [InlineData("bound naming no limit", "$.messages[0].fields[0].minimum")]
    [InlineData("lessThan naming a struct field", "$.messages[0].fields")]
    [InlineData("array above 255 elements", "$.messages[0].fields[1].maxCount")]
    [InlineData("distinctBy naming no field", "$.messages[0].fields[1].distinctBy")]
    [InlineData("nine bits", "$.structs[0].fields[2].bits")]
    [InlineData("duplicate field", "$.messages[0].fields")]
    [InlineData("unwrap with two fields", "$.messages[0].api")]
    [InlineData("unreliable message above its limit", "$.messages[0].fields")]
    [InlineData("reliable message above the packet limit", "$.messages[0].runtimeSizeCheck")]
    [InlineData("size constant on a variable message", "$.messages[0].sizeConstant")]
    [InlineData("struct that contains itself", "$")]
    [InlineData("unused struct", "$")]
    [InlineData("size constant used twice", "$")]
    [InlineData("acceptOnDecode on a number", "$.messages[0].fields[0].blank")]
    [InlineData("acceptOnDecode on a struct field", "$.structs[0].fields")]
    [InlineData("acceptOnDecode on a struct with a nested struct", "$.messages[0].fields")]
    [InlineData("blank allow on a struct field", "$.messages[0].fields[1].blank")]
    [InlineData("unknown blank value", "$.messages[0].fields[0].blank")]
    [InlineData("nullAsEmpty on required text", "$.messages[0].fields[3].nullAsEmpty")]
    [InlineData("nullAsEmpty on an array with minCount", "$.messages[0].fields[1].nullAsEmpty")]
    [InlineData("optional number", "$.messages[0].fields[0].optional")]
    [InlineData("optional with a condition", "$.messages[0].fields[4].when")]
    [InlineData("runtime size check on a small message", "$.messages[0].runtimeSizeCheck")]
    [InlineData("expanded encode on a class message", "$.messages[0].encodeParameters")]
    [InlineData("empty message with rules", "$.messages[0].api")]
    [InlineData("empty message with fields", "$.messages[0].fields")]
    public void InvalidGeneratedSchemasAreRejectedWithTheirPath(string mutation, string path)
    {
        var schema = GeneratedSchema();
        var message = schema["messages"]![0]!.AsObject();
        var fields = message["fields"]!.AsArray();
        var structFields = schema["structs"]![0]!["fields"]!.AsArray();
        switch (mutation)
        {
            case "unknown field type":
                fields[0]!["type"] = "u24";
                break;
            case "rule that does not fit the type":
                fields[0]!["nonEmpty"] = true;
                break;
            case "bound outside the field type":
                fields[0]!["maximum"] = 70000;
                fields[0]!["type"] = "u16";
                break;
            case "bound naming no limit":
                fields[0]!["minimum"] = "MaximumMissing";
                break;
            case "lessThan naming a struct field":
                fields[0]!["lessThan"] = "Points";
                break;
            case "array above 255 elements":
                fields[1]!["maxCount"] = 256;
                break;
            case "distinctBy naming no field":
                fields[1]!["distinctBy"] = "Z";
                break;
            case "nine bits":
                var bits = structFields[2]!["bits"]!.AsArray();
                for (var index = 0; index < 8; index++)
                {
                    bits.Add(new JsonObject { ["name"] = "Bit" + index });
                }

                break;
            case "duplicate field":
                fields.Add(fields[0]!.DeepClone());
                break;
            case "unwrap with two fields":
                message["api"] = "unwrap";
                break;
            case "unreliable message above its limit":
                fields[1]!["maxCount"] = 255;
                schema["limits"]![0]!["value"] = 255;
                fields[1]!["maxCount"] = "MaximumThings";
                schema["protocol"]!["maximumUnreliablePacketSize"] = 100;
                message.Remove("emptySizeConstant");
                break;
            case "reliable message above the packet limit":
                message["delivery"] = "reliableOrdered";
                schema["limits"]![0]!["value"] = 255;
                schema["protocol"]!["maximumPacketSize"] = 1000;
                schema["protocol"]!["maximumUnreliablePacketSize"] = 1000;
                break;
            case "size constant on a variable message":
                message["sizeConstant"] = "SnapshotSize";
                break;
            case "struct that contains itself":
                structFields.Add(new JsonObject { ["name"] = "Next", ["type"] = "Point" });
                break;
            case "unused struct":
                var unused = schema["structs"]![0]!.DeepClone().AsObject();
                unused["name"] = "Unused";
                unused.Remove("sizeConstant");
                schema["structs"]!.AsArray().Add(unused);
                break;
            case "size constant used twice":
                message["emptySizeConstant"] = "PointSize";
                break;
            case "acceptOnDecode on a number":
                fields[0]!["blank"] = "acceptOnDecode";
                break;
            case "acceptOnDecode on a struct field":
                structFields.Add(JsonNode.Parse("""{ "name": "Tag", "type": "string", "maxLength": 4, "blank": "acceptOnDecode" }"""));
                break;
            case "acceptOnDecode on a struct with a nested struct":
                schema["structs"]!.AsArray().Add(JsonNode.Parse(
                    """{ "name": "Wrapper", "family": "Input", "description": "A wrapper.", "fields": [ { "name": "Inner", "type": "Point" } ] }"""));
                fields.Add(JsonNode.Parse("""{ "name": "Wrapped", "type": "Wrapper", "blank": "acceptOnDecode" }"""));
                break;
            case "blank allow on a struct field":
                fields[1]!["type"] = "Point";
                fields[1]!.AsObject().Remove("items");
                fields[1]!.AsObject().Remove("minCount");
                fields[1]!.AsObject().Remove("maxCount");
                fields[1]!["blank"] = "allow";
                break;
            case "unknown blank value":
                fields[0]!["blank"] = "maybe";
                break;
            case "nullAsEmpty on required text":
                fields.Add(JsonNode.Parse("""{ "name": "Label", "type": "string", "maxLength": 4, "nullAsEmpty": true }"""));
                break;
            case "nullAsEmpty on an array with minCount":
                fields[1]!["nullAsEmpty"] = true;
                break;
            case "optional number":
                fields[0]!["optional"] = true;
                break;
            case "optional with a condition":
                fields.Add(JsonNode.Parse("""{ "name": "Failed", "type": "bool" }"""));
                fields.Add(JsonNode.Parse("""{ "name": "Origin", "type": "Point", "optional": true, "when": "Failed" }"""));
                break;
            case "runtime size check on a small message":
                message["delivery"] = "reliableOrdered";
                message["runtimeSizeCheck"] = true;
                break;
            case "expanded encode on a class message":
                message["encodeParameters"] = "expanded";
                break;
            case "empty message with rules":
                message["api"] = "empty";
                break;
            case "empty message with fields":
                message["api"] = "empty";
                message.Remove("rules");
                message.Remove("parameter");
                message.Remove("emptySizeConstant");
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
                  "fields": [ { "name": "Sequence", "type": "u32" } ],
                  "description": "A snapshot."
                }
              ]
            }
            """)!.AsObject();
    }

    // The minimal schema with one struct and one generated message that uses it.
    private static JsonObject GeneratedSchema()
    {
        var schema = MinimalSchema();
        schema["structs"] = JsonNode.Parse(
            """
            [
              {
                "name": "Point",
                "family": "Input",
                "description": "A point.",
                "sizeConstant": "PointSize",
                "fields": [
                  { "name": "X", "type": "f32", "exclusiveMinimum": 0 },
                  { "name": "Y", "type": "f32" },
                  { "name": "Flags", "type": "bits8", "bits": [ { "name": "IsActive" } ] }
                ]
              }
            ]
            """);
        var message = schema["messages"]![0]!.AsObject();
        message["parameter"] = "snapshot";
        message["rules"] = true;
        message["emptySizeConstant"] = "EmptySnapshotSize";
        message["fields"] = JsonNode.Parse(
            """
            [
              { "name": "Sequence", "type": "u32", "minimum": 1 },
              { "name": "Points", "type": "array", "items": "Point", "minCount": 1, "maxCount": "MaximumThings" },
              { "name": "Held", "type": "Buttons" }
            ]
            """);
        return schema;
    }

    // The minimal schema with one generated message whose Kind field selects its variant.
    private static JsonObject UnionSchema()
    {
        var schema = MinimalSchema();
        schema["enums"]!.AsArray().Add(JsonNode.Parse(
            """
            {
              "name": "Slot",
              "family": "Input",
              "description": "A slot.",
              "values": [ { "name": "Count", "value": 1 }, { "name": "Target", "value": 2 } ]
            }
            """));
        var message = schema["messages"]![0]!.AsObject();
        message["parameter"] = "snapshot";
        message["fields"] = JsonNode.Parse(
            """
            [
              { "name": "Id", "type": "guid", "nonEmpty": true },
              { "name": "Kind", "type": "Slot" }
            ]
            """);
        message["variants"] = JsonNode.Parse(
            """
            {
              "discriminator": "Kind",
              "fields": [
                { "name": "Amount", "type": "i32", "absent": -1 },
                { "name": "Target", "type": "guid" }
              ],
              "cases": [
                {
                  "kinds": [ { "kind": "Count", "factory": "CreateCount" } ],
                  "fields": [ { "field": "Amount", "minimum": 0, "parameterDefault": -1 } ]
                },
                {
                  "kinds": [ { "kind": "Target", "factory": "CreateTarget" } ],
                  "fields": [ { "group": [ { "field": "Target" } ], "presence": "Target" } ]
                }
              ]
            }
            """);
        return schema;
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

        public void EditSchema(Action<JsonObject> edit)
        {
            var path = ProtocolLayout.Resolve(Root, ProtocolLayout.SchemaPath);
            var schema = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            edit(schema);
            File.WriteAllText(path, schema.ToJsonString());
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
