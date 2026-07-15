using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using ShooterMmo.WorldData.Editor.Items;
using ShooterMmo.WorldData.Items;
using ShooterMmo.WorldData.Items.Presentation;
using UnityEditor.Compilation;

namespace ShooterMmo.Tests.EditMode
{
    public sealed class ItemCatalogEditorTests
    {
        [Test]
        public void CheckedInAuthoringRoundTripsWithoutSemanticChanges()
        {
            var workspace = LoadCheckedInWorkspace();
            var firstCatalog = ItemCatalogCompiler.Compile(
                workspace.Authoring.ToDomainDocument());
            var roundTripped = ItemCatalogEditorJson.DeserializeAuthoring(
                ItemCatalogEditorJson.SerializeAuthoring(workspace.Authoring));
            var secondCatalog = ItemCatalogCompiler.Compile(
                roundTripped.ToDomainDocument());

            Assert.That(secondCatalog.Revision, Is.EqualTo(firstCatalog.Revision));
            Assert.That(
                secondCatalog.Definitions.Select(definition => definition.StructuralFingerprint),
                Is.EqualTo(firstCatalog.Definitions.Select(
                    definition => definition.StructuralFingerprint)));
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
                var source = File.ReadAllText(paths.AuthoringPath);
                var integerWeight = "\"unitWeight\": 1";
                var weightIndex = source.IndexOf(integerWeight, StringComparison.Ordinal);
                Assert.That(weightIndex, Is.GreaterThanOrEqualTo(0));
                var invalidSource = source.Remove(weightIndex, integerWeight.Length)
                    .Insert(weightIndex, "\"unitWeight\": 1.5");
                var authoringPath = Path.Combine(temporaryDirectory, "decimal-authoring.json");
                var runtimePath = Path.Combine(temporaryDirectory, "runtime.json");
                File.WriteAllText(authoringPath, invalidSource);
                var runner = new ItemCatalogCompilerProcessRunner(
                    paths.RepositoryRoot,
                    paths.CompilerProjectPath);

                var result = runner.Compile(authoringPath, runtimePath);

                Assert.That(result.Success, Is.False);
                Assert.That(result.Output, Does.Contain("unitWeight").IgnoreCase);
                Assert.That(File.Exists(runtimePath), Is.False);
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
                "authoring",
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
                var authoringPath = Path.Combine(temporaryDirectory, "editor-authoring.json");
                var firstRuntimePath = Path.Combine(temporaryDirectory, "first-runtime.json");
                var secondRuntimePath = Path.Combine(temporaryDirectory, "second-runtime.json");
                File.WriteAllText(
                    authoringPath,
                    ItemCatalogEditorJson.SerializeAuthoring(workspace.Authoring));
                var runner = new ItemCatalogCompilerProcessRunner(
                    paths.RepositoryRoot,
                    paths.CompilerProjectPath);

                var firstResult = runner.Compile(authoringPath, firstRuntimePath);
                var secondResult = runner.Compile(authoringPath, secondRuntimePath);
                var verifyResult = runner.Verify(authoringPath, firstRuntimePath);

                Assert.That(firstResult.Success, Is.True, firstResult.Output);
                Assert.That(secondResult.Success, Is.True, secondResult.Output);
                Assert.That(verifyResult.Success, Is.True, verifyResult.Output);
                Assert.That(
                    File.ReadAllBytes(secondRuntimePath),
                    Is.EqualTo(File.ReadAllBytes(firstRuntimePath)));
                var firstRuntime = ItemCatalogEditorJson.DeserializeRuntime(
                    File.ReadAllText(firstRuntimePath));
                var secondRuntime = ItemCatalogEditorJson.DeserializeRuntime(
                    File.ReadAllText(secondRuntimePath));
                Assert.That(secondRuntime.revision, Is.EqualTo(firstRuntime.revision));
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
                var authoringPath = Path.Combine(temporaryDirectory, "authoring.json");
                var runtimePath = Path.Combine(temporaryDirectory, "runtime.json");
                var presentationPath = Path.Combine(temporaryDirectory, "presentation.json");
                File.WriteAllText(authoringPath, "original authoring");
                File.WriteAllText(runtimePath, "original runtime");
                File.WriteAllText(presentationPath, "original presentation");
                var paths = new ItemCatalogEditorPaths(
                    temporaryDirectory,
                    authoringPath,
                    runtimePath,
                    presentationPath,
                    Path.Combine(temporaryDirectory, "compiler.csproj"));
                var service = new ItemCatalogEditorService(paths, new FailingCompilerRunner());

                var validation = service.Validate(workspace);

                Assert.That(validation.Success, Is.False);
                Assert.That(validation.Message, Does.Contain("definition field"));
                Assert.Throws<InvalidOperationException>(() => service.Save(workspace, validation));
                Assert.Throws<InvalidOperationException>(
                    () => service.SaveAndBake(workspace, validation));
                Assert.That(File.ReadAllText(authoringPath), Is.EqualTo("original authoring"));
                Assert.That(File.ReadAllText(runtimePath), Is.EqualTo("original runtime"));
                Assert.That(
                    File.ReadAllText(presentationPath),
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
                var authoringPath = Path.Combine(temporaryDirectory, "authoring.json");
                var runtimePath = Path.Combine(temporaryDirectory, "runtime.json");
                var blockedPresentationPath = Path.Combine(
                    temporaryDirectory,
                    "presentation-is-a-directory");
                File.WriteAllText(authoringPath, "original authoring");
                File.WriteAllText(runtimePath, "original runtime");
                Directory.CreateDirectory(blockedPresentationPath);
                var paths = new ItemCatalogEditorPaths(
                    temporaryDirectory,
                    authoringPath,
                    runtimePath,
                    blockedPresentationPath,
                    Path.Combine(temporaryDirectory, "compiler.csproj"));
                var service = new ItemCatalogEditorService(paths, new FailingCompilerRunner());
                var validation = ItemCatalogEditorValidationResult.Passed(
                    "valid",
                    "new authoring",
                    "new runtime",
                    "new presentation",
                    workspace.CurrentRuntime,
                    workspace.Presentation,
                    Array.Empty<ItemCatalogDefinitionChange>());

                Assert.Throws<IOException>(
                    () => service.SaveAndBake(workspace, validation));
                Assert.That(File.ReadAllText(authoringPath), Is.EqualTo("original authoring"));
                Assert.That(File.ReadAllText(runtimePath), Is.EqualTo("original runtime"));
                Assert.That(Directory.Exists(blockedPresentationPath), Is.True);
            }
            finally
            {
                DeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        [Test]
        public void RuntimeWorldDataAssembliesDoNotReferenceUnityEditor()
        {
            var runtimeAssemblyNames = new[]
            {
                "ShooterMmo.WorldData",
                "ShooterMmo.WorldData.Client"
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
                        iconResourcePath = string.Empty,
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
            changedEntries[0].iconResourcePath = "Items/Icons/test-ammunition";
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
                ItemCatalogEditorJson.DeserializeAuthoring(
                    File.ReadAllText(paths.AuthoringPath)),
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
                iconResourcePath = entry.iconResourcePath,
                localizationKey = entry.localizationKey,
                fallbackDisplayName = entry.fallbackDisplayName,
                prefabPresentationKey = entry.prefabPresentationKey
            };
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "ShooterMmoItemCatalogTests",
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

        private sealed class FailingCompilerRunner : IItemCatalogCompilerRunner
        {
            public ItemCatalogCompilerRunResult Compile(
                string authoringPath,
                string runtimePath)
            {
                return new ItemCatalogCompilerRunResult(
                    false,
                    "definitions[0].unitWeight has an invalid definition field.");
            }
        }
    }
}
