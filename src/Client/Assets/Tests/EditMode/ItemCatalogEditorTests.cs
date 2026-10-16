using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Content;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Editor.Items;
using Mmorpg.WorldData.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class ItemCatalogEditorTests
    {
        [Test]
        public void CheckedInAuthoringRoundTripsWithoutSemanticChanges()
        {
            var workspace = LoadCheckedInWorkspace();
            var firstCatalog = ItemCatalogCompiler.Compile(
                workspace.Authoring.ToDomainDocument());
            var temporaryDirectory = CreateTemporaryDirectory();
            ItemCatalogEditorDocument roundTripped;
            try
            {
                WriteFiles(
                    temporaryDirectory,
                    ItemCatalogEditorContent.CreateFiles(workspace.Authoring));
                roundTripped = ItemCatalogEditorContent.Load(temporaryDirectory);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }

            var secondCatalog = ItemCatalogCompiler.Compile(
                roundTripped.ToDomainDocument());

            Assert.That(secondCatalog.Revision, Is.EqualTo(firstCatalog.Revision));
            Assert.That(
                secondCatalog.Definitions.Select(definition => definition.StructuralFingerprint),
                Is.EqualTo(firstCatalog.Definitions.Select(
                    definition => definition.StructuralFingerprint)));
        }

        [Test]
        public void CheckedInItemContentIsInCanonicalEditorFormat()
        {
            var paths = ItemCatalogEditorPaths.CreateDefault();
            var expected = ItemCatalogEditorContent.CreateFiles(
                ItemCatalogEditorContent.Load(paths.ItemsDirectory));
            var actual = Directory.EnumerateFiles(
                    paths.ItemsDirectory,
                    "*.json",
                    SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(paths.ItemsDirectory, path),
                    path => File.ReadAllText(path).Replace("\r\n", "\n"));

            Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
            foreach (var file in expected)
            {
                Assert.That(actual[file.Key], Is.EqualTo(file.Value), file.Key);
            }
        }

        [Test]
        public void EditorCreatedBagItemPassesSharedCompiler()
        {
            var workspace = LoadCheckedInWorkspace();
            var definition = workspace.CreateDefinition();
            Assert.That(workspace.TryRenameDraftDefinition(
                definition,
                "bag.editor_test_pack",
                out var renameError), Is.True, renameError);
            definition.displayName = "Editor Test Pack";
            definition.category = ItemCategoryIds.Bag;
            definition.tags = Array.Empty<string>();
            definition.unitWeight = 10;
            definition.maximumStackSize = 1;
            definition.equipmentSlots = new[] { ItemEquipmentSlotIds.Bag };
            definition.playerDestroyable = true;
            definition.locationEligibility.secureContainer = false;
            definition.defaultPolicies = new[] { ItemPolicyIds.Insured };
            definition.bag = new ItemCatalogEditorBagDefinition
            {
                carryCapacityBonus = 40,
                slots = new[]
                {
                    new ItemCatalogEditorBagSlot
                    {
                        index = 0,
                        kind = BagSlotKindIds.General,
                        acceptedTags = Array.Empty<string>()
                    },
                    new ItemCatalogEditorBagSlot
                    {
                        index = 1,
                        kind = BagSlotKindIds.Specialized,
                        acceptedTags = new[] { ItemTagIds.Ammunition }
                    }
                }
            };

            var catalog = ItemCatalogCompiler.Compile(workspace.Authoring.ToDomainDocument());

            Assert.That(catalog.Definitions.Any(candidate => string.Equals(
                candidate.Id,
                definition.id,
                StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void InvalidDefinitionsAreRejectedWithFieldSpecificErrors()
        {
            AssertInvalidCatalog(
                document => document.definitions = document.definitions.Concat(
                    new[] { document.definitions[0] }).ToArray(),
                "duplicate id");
            AssertInvalidCatalog(
                document => document.definitions[0].category = "unknown_category",
                ".category");
            AssertInvalidCatalog(
                document => document.definitions[0].unitWeight = -1,
                ".unitWeight");
            AssertInvalidCatalog(
                document => document.definitions[0].maximumStackSize = 0,
                ".maximumStackSize");
            AssertInvalidCatalog(
                document => document.definitions.First(definition => definition.bag != null)
                    .bag.slots[1].index = 0,
                "duplicate index");
        }

        [Test]
        public void DecimalWeightIsRejectedByStrictCommandLineCompiler()
        {
            var paths = ItemCatalogEditorPaths.CreateDefault();
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var generatedRoot = Path.Combine(temporaryDirectory, "generated");
                var itemsDirectory = ContentLayout.ItemsDirectory(contentRoot);
                WriteFiles(
                    itemsDirectory,
                    ItemCatalogEditorContent.CreateFiles(
                        ItemCatalogEditorContent.Load(paths.ItemsDirectory)));
                var definitionPath = ContentLayout.DefinitionPath(
                    itemsDirectory,
                    "ammunition.training_556");
                var source = File.ReadAllText(definitionPath);
                const string integerWeight = "\"unitWeight\": 1";
                Assert.That(source, Does.Contain(integerWeight));
                File.WriteAllText(
                    definitionPath,
                    source.Replace(integerWeight, "\"unitWeight\": 1.5"));
                var runner = new ContentCompilerProcessRunner(
                    paths.RepositoryRoot,
                    ItemCatalogEditorService.CompilerToolName);

                var result = runner.Compile(contentRoot, generatedRoot);

                Assert.That(result.Success, Is.False);
                Assert.That(result.Output, Does.Contain("unitWeight").IgnoreCase);
                Assert.That(
                    File.Exists(ContentLayout.GeneratedItemCatalogPath(generatedRoot)),
                    Is.False);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void BakedDefinitionIdCannotBeChangedRemovedOrReused()
        {
            var workspace = LoadCheckedInWorkspace();
            var baked = workspace.Authoring.definitions[0];

            Assert.That(workspace.IsDefinitionIdLocked(baked.id), Is.True);
            Assert.That(workspace.TryRenameDraftDefinition(
                baked,
                "different.id",
                out var renameError), Is.False);
            Assert.That(renameError, Does.Contain("cannot be changed"));
            Assert.That(workspace.TryRemoveDraftDefinition(
                baked,
                out var removeError), Is.False);
            Assert.That(removeError, Does.Contain("cannot be removed"));

            var draft = workspace.CreateDefinition();
            Assert.That(workspace.TryRenameDraftDefinition(
                draft,
                baked.id,
                out var reuseError), Is.False);
            Assert.That(reuseError, Does.Contain("already uses id"));
        }

        [Test]
        public void DisplayOnlyAndStructuralChangesAreClassifiedCorrectly()
        {
            var workspace = LoadCheckedInWorkspace();
            var baseline = ItemCatalogCompiler.Compile(workspace.Authoring.ToDomainDocument());
            var definitionId = workspace.Authoring.definitions[0].id;
            var baselineDefinition = FindDefinition(baseline, definitionId);

            var displayDocument = ItemCatalogEditorJson.Clone(workspace.Authoring);
            displayDocument.definitions[0].displayName += " Updated";
            var displayCatalog = ItemCatalogCompiler.Compile(displayDocument.ToDomainDocument());
            var displayDefinition = FindDefinition(displayCatalog, definitionId);
            var displayChanges = ItemCatalogChangeClassifier.Compare(
                ItemCatalogEditorRuntimeDocument.FromDomain(baseline),
                ItemCatalogEditorRuntimeDocument.FromDomain(displayCatalog));

            Assert.That(displayCatalog.Revision, Is.Not.EqualTo(baseline.Revision));
            Assert.That(
                displayDefinition.StructuralFingerprint,
                Is.EqualTo(baselineDefinition.StructuralFingerprint));
            Assert.That(displayChanges.Single().Kind,
                Is.EqualTo(ItemCatalogDefinitionChangeKind.DisplayOnly));

            var structuralDocument = ItemCatalogEditorJson.Clone(workspace.Authoring);
            structuralDocument.definitions[0].unitWeight++;
            var structuralCatalog = ItemCatalogCompiler.Compile(
                structuralDocument.ToDomainDocument());
            var structuralDefinition = FindDefinition(structuralCatalog, definitionId);
            var structuralRuntime = ItemCatalogEditorRuntimeDocument.FromDomain(
                structuralCatalog);
            var structuralChanges = ItemCatalogChangeClassifier.Compare(
                ItemCatalogEditorRuntimeDocument.FromDomain(baseline),
                structuralRuntime);
            var validation = ItemCatalogEditorValidationResult.Passed(
                "valid",
                new Dictionary<string, string>(),
                "runtime",
                "presentation",
                structuralRuntime,
                workspace.Presentation,
                structuralChanges);

            Assert.That(
                structuralDefinition.StructuralFingerprint,
                Is.Not.EqualTo(baselineDefinition.StructuralFingerprint));
            Assert.That(structuralChanges.Single().Kind,
                Is.EqualTo(ItemCatalogDefinitionChangeKind.Structural));
            Assert.That(validation.RequiresStructuralConfirmation, Is.True);
        }

        [Test]
        public void IdenticalEditorContentBakesDeterministicallyAndPassesCliVerify()
        {
            var paths = ItemCatalogEditorPaths.CreateDefault();
            var workspace = LoadCheckedInWorkspace();
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var contentRoot = Path.Combine(temporaryDirectory, "content");
                var firstGeneratedRoot = Path.Combine(temporaryDirectory, "first");
                var secondGeneratedRoot = Path.Combine(temporaryDirectory, "second");
                WriteFiles(
                    ContentLayout.ItemsDirectory(contentRoot),
                    ItemCatalogEditorContent.CreateFiles(workspace.Authoring));
                var runner = new ContentCompilerProcessRunner(
                    paths.RepositoryRoot,
                    ItemCatalogEditorService.CompilerToolName);

                var firstResult = runner.Compile(contentRoot, firstGeneratedRoot);
                var secondResult = runner.Compile(contentRoot, secondGeneratedRoot);
                var verifyResult = runner.Verify(contentRoot, firstGeneratedRoot);

                Assert.That(firstResult.Success, Is.True, firstResult.Output);
                Assert.That(secondResult.Success, Is.True, secondResult.Output);
                Assert.That(verifyResult.Success, Is.True, verifyResult.Output);
                var firstRuntimePath = ContentLayout.GeneratedItemCatalogPath(firstGeneratedRoot);
                var secondRuntimePath = ContentLayout.GeneratedItemCatalogPath(secondGeneratedRoot);
                Assert.That(
                    File.ReadAllBytes(secondRuntimePath),
                    Is.EqualTo(File.ReadAllBytes(firstRuntimePath)));
                Assert.That(
                    File.ReadAllText(firstRuntimePath),
                    Is.EqualTo(File.ReadAllText(paths.RuntimePath).Replace("\r\n", "\n")));
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void FailedValidationLeavesCanonicalCandidatesUnchanged()
        {
            var workspace = LoadCheckedInWorkspace();
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var paths = CreateTemporaryPaths(temporaryDirectory);
                var catalogPath = ContentLayout.CatalogPath(paths.ItemsDirectory);
                WriteText(catalogPath, "original authoring");
                WriteText(paths.RuntimePath, "original runtime");
                WriteText(paths.PresentationPath, "original presentation");
                var service = new ItemCatalogEditorService(paths, new FailingCompilerRunner());

                var validation = service.Validate(workspace);

                Assert.That(validation.Success, Is.False);
                Assert.That(validation.Message, Does.Contain("definition field"));
                Assert.Throws<InvalidOperationException>(() => service.Save(workspace, validation));
                Assert.Throws<InvalidOperationException>(
                    () => service.SaveAndBake(workspace, validation));
                Assert.That(File.ReadAllText(catalogPath), Is.EqualTo("original authoring"));
                Assert.That(File.ReadAllText(paths.RuntimePath), Is.EqualTo("original runtime"));
                Assert.That(
                    File.ReadAllText(paths.PresentationPath),
                    Is.EqualTo("original presentation"));
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void FailedBakeRollsBackEveryCatalogFile()
        {
            var workspace = LoadCheckedInWorkspace();
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var paths = CreateTemporaryPaths(temporaryDirectory);
                var catalogPath = ContentLayout.CatalogPath(paths.ItemsDirectory);
                var removedDefinitionPath = ContentLayout.DefinitionPath(
                    paths.ItemsDirectory,
                    "material.old_ore");
                var addedDefinitionPath = ContentLayout.DefinitionPath(
                    paths.ItemsDirectory,
                    "material.new_ore");
                WriteText(catalogPath, "original authoring");
                WriteText(removedDefinitionPath, "original definition");
                WriteText(paths.RuntimePath, "original runtime");
                Directory.CreateDirectory(paths.PresentationPath);
                var service = new ItemCatalogEditorService(paths, new FailingCompilerRunner());
                var validation = ItemCatalogEditorValidationResult.Passed(
                    "valid",
                    new Dictionary<string, string>
                    {
                        [ContentLayout.CatalogFileName] = "new authoring",
                        [Path.Combine(
                            ContentLayout.DefinitionsDirectoryName,
                            "material.new_ore.json")] = "new definition"
                    },
                    "new runtime",
                    "new presentation",
                    workspace.CurrentRuntime,
                    workspace.Presentation,
                    Array.Empty<ItemCatalogDefinitionChange>());

                Assert.Throws<IOException>(
                    () => service.SaveAndBake(workspace, validation));
                Assert.That(File.ReadAllText(catalogPath), Is.EqualTo("original authoring"));
                Assert.That(
                    File.ReadAllText(removedDefinitionPath),
                    Is.EqualTo("original definition"));
                Assert.That(File.Exists(addedDefinitionPath), Is.False);
                Assert.That(File.ReadAllText(paths.RuntimePath), Is.EqualTo("original runtime"));
                Assert.That(Directory.Exists(paths.PresentationPath), Is.True);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void SaveWritesOneFilePerDefinitionAndDeletesRemovedDefinitions()
        {
            var workspace = LoadCheckedInWorkspace();
            var temporaryDirectory = CreateTemporaryDirectory();
            try
            {
                var paths = CreateTemporaryPaths(temporaryDirectory);
                var removedDefinitionPath = ContentLayout.DefinitionPath(
                    paths.ItemsDirectory,
                    "material.removed_draft");
                WriteText(removedDefinitionPath, "saved draft");
                WriteText(paths.RuntimePath, "original runtime");
                var service = new ItemCatalogEditorService(paths, new FailingCompilerRunner());
                var authoringFiles = ItemCatalogEditorContent.CreateFiles(workspace.Authoring);
                var validation = ItemCatalogEditorValidationResult.Passed(
                    "valid",
                    authoringFiles,
                    "new runtime",
                    "new presentation",
                    workspace.CurrentRuntime,
                    workspace.Presentation,
                    Array.Empty<ItemCatalogDefinitionChange>());

                service.Save(workspace, validation);

                Assert.That(File.Exists(removedDefinitionPath), Is.False);
                Assert.That(File.ReadAllText(paths.RuntimePath), Is.EqualTo("original runtime"));
                Assert.That(File.ReadAllText(paths.PresentationPath), Is.EqualTo("new presentation"));
                var reloaded = ItemCatalogEditorContent.Load(paths.ItemsDirectory);
                Assert.That(
                    reloaded.definitions.Select(definition => definition.id),
                    Is.EqualTo(workspace.Authoring.definitions
                        .Select(definition => definition.id)
                        .OrderBy(id => id, StringComparer.Ordinal)));
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void DefinitionIdsThatCannotNameFilesFailBeforeAnyWrite()
        {
            var workspace = LoadCheckedInWorkspace();
            var draft = workspace.CreateDefinition();
            draft.id = "../outside";

            Assert.Throws<InvalidDataException>(
                () => ItemCatalogEditorContent.CreateFiles(workspace.Authoring));
        }

        [Test]
        public void RuntimeWorldDataAssembliesDoNotReferenceUnityEditor()
        {
            var runtimeAssemblyNames = new[]
            {
                "Mmorpg.WorldData",
                "Mmorpg.WorldData.Client"
            };
            foreach (var assemblyName in runtimeAssemblyNames)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(candidate =>
                    string.Equals(
                        candidate.GetName().Name,
                        assemblyName,
                        StringComparison.Ordinal));
                Assert.That(
                    assembly.GetReferencedAssemblies().Any(reference =>
                        reference.Name.StartsWith("UnityEditor", StringComparison.Ordinal)),
                    Is.False,
                    assemblyName + " must not reference UnityEditor assemblies.");
                var compiledAssembly = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                    .Single(candidate => string.Equals(
                        candidate.name,
                        assemblyName,
                        StringComparison.Ordinal));
                foreach (var sourceFile in compiledAssembly.sourceFiles)
                {
                    Assert.That(
                        File.ReadAllText(sourceFile),
                        Does.Not.Contain("using UnityEditor"),
                        sourceFile);
                }
            }
        }

        [Test]
        public void CheckedInPresentationHasExactCoverageAndDeterministicRevision()
        {
            var workspace = LoadCheckedInWorkspace();
            var expectedIds = workspace.CurrentRuntime.definitions
                .Select(definition => definition.id)
                .ToArray();
            var compiled = ItemPresentationCatalogCompiler.Compile(
                workspace.CurrentRuntime.revision,
                expectedIds,
                workspace.Presentation.entries);

            Assert.That(compiled.entries.Select(entry => entry.definitionId),
                Is.EquivalentTo(expectedIds));
            Assert.That(compiled.entries.GroupBy(entry => entry.definitionId),
                Has.All.Matches<IGrouping<string, ItemPresentationEntry>>(
                    group => group.Count() == 1));
            Assert.That(
                workspace.Presentation.sourceCatalogRevision,
                Is.EqualTo(workspace.CurrentRuntime.revision));
            Assert.That(
                workspace.Presentation.presentationRevision,
                Is.EqualTo(compiled.presentationRevision));

            Assert.That(ItemPresentationCatalogIndex.TryCreate(
                workspace.Presentation,
                workspace.CurrentRuntime.revision,
                expectedIds,
                out var index,
                out var error), Is.True, error);
            Assert.That(index, Is.Not.Null);
        }

        [Test]
        public void DuplicateUnknownAndMissingPresentationIdsFailValidation()
        {
            var workspace = LoadCheckedInWorkspace();
            var expectedIds = workspace.CurrentRuntime.definitions
                .Select(definition => definition.id)
                .ToArray();

            AssertPresentationInvalid(
                workspace,
                expectedIds,
                entries => entries.Concat(new[] { CloneEntry(entries[0]) }).ToArray(),
                "duplicate definition id");
            AssertPresentationInvalid(
                workspace,
                expectedIds,
                entries => entries.Concat(new[]
                {
                    new ItemPresentationEntry
                    {
                        definitionId = "unknown.definition",
                        iconAddress = string.Empty,
                        localizationKey = "items.unknown.definition.name",
                        fallbackDisplayName = "Unknown",
                        prefabPresentationKey = string.Empty
                    }
                }).ToArray(),
                "unknown definition");
            AssertPresentationInvalid(
                workspace,
                expectedIds,
                entries => entries.Skip(1).ToArray(),
                "Missing presentation entry");
        }

        [Test]
        public void ClientOnlyPresentationChangesAdvanceOnlyPresentationRevision()
        {
            var workspace = LoadCheckedInWorkspace();
            var gameplayBefore = ItemCatalogCompiler.Compile(
                workspace.Authoring.ToDomainDocument());
            var expectedIds = gameplayBefore.Definitions
                .Select(definition => definition.Id)
                .ToArray();
            var presentationBefore = ItemPresentationCatalogCompiler.Compile(
                gameplayBefore.Revision,
                expectedIds,
                workspace.Presentation.entries);
            var changedEntries = presentationBefore.entries.Select(CloneEntry).ToArray();
            changedEntries[0].iconAddress = "Items/Icons/test-ammunition.png";
            var presentationAfter = ItemPresentationCatalogCompiler.Compile(
                gameplayBefore.Revision,
                expectedIds,
                changedEntries);
            var gameplayAfter = ItemCatalogCompiler.Compile(
                workspace.Authoring.ToDomainDocument());

            Assert.That(gameplayAfter.Revision, Is.EqualTo(gameplayBefore.Revision));
            Assert.That(
                gameplayAfter.Definitions.Select(definition => definition.StructuralFingerprint),
                Is.EqualTo(gameplayBefore.Definitions.Select(
                    definition => definition.StructuralFingerprint)));
            Assert.That(
                presentationAfter.presentationRevision,
                Is.Not.EqualTo(presentationBefore.presentationRevision));
            Assert.That(
                presentationAfter.sourceCatalogRevision,
                Is.EqualTo(gameplayBefore.Revision));
        }

        [Test]
        public void PresentationLoaderCachesValidatedClientCatalog()
        {
            var workspace = LoadCheckedInWorkspace();
            var expectedIds = workspace.CurrentRuntime.definitions
                .Select(definition => definition.id)
                .ToArray();
            ItemPresentationCatalogLoader.ResetCache();

            Assert.That(ItemPresentationCatalogLoader.TryLoadOnce(
                workspace.CurrentRuntime.revision,
                expectedIds,
                out var first,
                out var firstError), Is.True, firstError);
            Assert.That(ItemPresentationCatalogLoader.TryLoadOnce(
                workspace.CurrentRuntime.revision,
                expectedIds,
                out var second,
                out var secondError), Is.True, secondError);
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void SuccessfulBakeLocksNewDefinitionIdInCurrentWorkspace()
        {
            var workspace = LoadCheckedInWorkspace();
            var draft = workspace.CreateDefinition();
            var bakedDefinitions = workspace.CurrentRuntime.definitions.Concat(new[]
            {
                new ItemCatalogEditorRuntimeDefinition
                {
                    id = draft.id,
                    displayName = draft.displayName,
                    structuralFingerprint = new string('a', 64)
                }
            }).ToArray();
            workspace.ApplyBakedRuntime(new ItemCatalogEditorRuntimeDocument
            {
                formatVersion = workspace.CurrentRuntime.formatVersion,
                catalogId = workspace.CurrentRuntime.catalogId,
                revision = new string('b', 64),
                definitions = bakedDefinitions
            });

            Assert.That(workspace.IsDefinitionIdLocked(draft.id), Is.True);
            Assert.That(workspace.TryRenameDraftDefinition(
                draft,
                "another.id",
                out _), Is.False);
        }

        private static ItemCatalogEditorWorkspace LoadCheckedInWorkspace()
        {
            var paths = ItemCatalogEditorPaths.CreateDefault();
            return new ItemCatalogEditorWorkspace(
                ItemCatalogEditorContent.Load(paths.ItemsDirectory),
                ItemCatalogEditorJson.DeserializeRuntime(
                    File.ReadAllText(paths.RuntimePath)),
                ItemPresentationCatalogJson.Deserialize(
                    File.ReadAllText(paths.PresentationPath)));
        }

        private static ItemDefinition FindDefinition(
            ItemCatalogRuntimeDocument catalog,
            string definitionId)
        {
            return catalog.Definitions.Single(definition => string.Equals(
                definition.Id,
                definitionId,
                StringComparison.Ordinal));
        }

        private static void AssertInvalidCatalog(
            Action<ItemCatalogEditorDocument> mutate,
            string expectedMessage)
        {
            var document = ItemCatalogEditorJson.Clone(LoadCheckedInWorkspace().Authoring);
            mutate(document);

            var exception = Assert.Throws<ItemCatalogValidationException>(
                () => ItemCatalogCompiler.Compile(document.ToDomainDocument()));
            Assert.That(exception.Message, Does.Contain(expectedMessage));
        }

        private static void AssertPresentationInvalid(
            ItemCatalogEditorWorkspace workspace,
            string[] expectedIds,
            Func<ItemPresentationEntry[], ItemPresentationEntry[]> mutate,
            string expectedMessage)
        {
            var entries = workspace.Presentation.entries.Select(CloneEntry).ToArray();
            var exception = Assert.Throws<ItemPresentationCatalogValidationException>(() =>
                ItemPresentationCatalogCompiler.Compile(
                    workspace.CurrentRuntime.revision,
                    expectedIds,
                    mutate(entries)));
            Assert.That(exception.Message, Does.Contain(expectedMessage).IgnoreCase);
        }

        private static ItemPresentationEntry CloneEntry(ItemPresentationEntry entry)
        {
            return new ItemPresentationEntry
            {
                definitionId = entry.definitionId,
                iconAddress = entry.iconAddress,
                localizationKey = entry.localizationKey,
                fallbackDisplayName = entry.fallbackDisplayName,
                prefabPresentationKey = entry.prefabPresentationKey
            };
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "MmorpgItemCatalogTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTemporaryDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static ItemCatalogEditorPaths CreateTemporaryPaths(string temporaryDirectory)
        {
            return new ItemCatalogEditorPaths(
                temporaryDirectory,
                Path.Combine(temporaryDirectory, "content"),
                Path.Combine(temporaryDirectory, "generated"),
                Path.Combine(temporaryDirectory, "presentation.json"));
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static void WriteFiles(
            string directory,
            IReadOnlyDictionary<string, string> files)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(directory, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, file.Value);
            }
        }

        private sealed class FailingCompilerRunner : IContentCompilerRunner
        {
            public ContentCompilerRunResult Compile(string contentRoot, string generatedRoot)
            {
                return new ContentCompilerRunResult(
                    false,
                    "definitions[0].unitWeight has an invalid definition field.");
            }

            public ContentCompilerRunResult Verify(string contentRoot, string generatedRoot)
            {
                return Compile(contentRoot, generatedRoot);
            }
        }
    }
}
