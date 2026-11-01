using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Mmorpg.WorldData.Editor;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    public sealed class InventoryItemGrantWindow : EditorWindow
    {
        public const string MenuPath = "MMORPG/Tools/Inventory Item Grants";

        private static readonly string[] DestinationLabels =
        {
            "Permanent Inventory",
            "Bank"
        };

        private static readonly string[] DestinationArguments =
        {
            "permanent",
            "bank"
        };

        private DevelopmentItemToolCliProcess activeProcess;
        private string activeCommandStatus = string.Empty;
        private bool activeProcessBuilding;
        private DevelopmentItemToolResponse response;
        private Vector2 scroll;
        private int characterIndex;
        private int definitionIndex;
        private int packageIndex;
        private int quantity = 1;
        private int destinationIndex;
        private string selectedCharacterId = string.Empty;
        private string statusMessage = "Refresh to load local development data.";
        private MessageType statusType = MessageType.Info;
        private bool initialRefreshScheduled;
        private long currencyAmount = 100;
        private string corpseShardId = "local-shard-1";
        private Vector3 corpsePosition = new Vector3(0f, 0f, -1f);

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var window = GetWindow<InventoryItemGrantWindow>();
            window.titleContent = new GUIContent("Inventory Item Grants");
            window.minSize = new Vector2(620f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += PollActiveProcess;
            if (!initialRefreshScheduled)
            {
                initialRefreshScheduled = true;
                EditorApplication.delayCall += RunInitialRefresh;
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollActiveProcess;
            EditorApplication.delayCall -= RunInitialRefresh;
            initialRefreshScheduled = false;
            if (activeProcess != null)
            {
                activeProcess.Dispose();
                activeProcess = null;
            }
        }

        private void OnGUI()
        {
            DrawToolbar();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "This Development-only tool sends grants through GameService and "
                    + "ItemTransactionService. It never writes directly to PostgreSQL. "
                    + "Each command first builds GameService in Release, so it never runs an "
                    + "older build. Item grants and corpse fixture sources must be offline.",
                MessageType.Info);

            if (response == null || response.data == null)
            {
                EditorGUILayout.HelpBox(statusMessage, statusType);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawCharacterSelection();
            EditorGUILayout.Space(8f);
            DrawCustomGrant();
            EditorGUILayout.Space(8f);
            DrawPackageGrant();
            EditorGUILayout.Space(8f);
            DrawCurrencyGrant();
            EditorGUILayout.Space(8f);
            DrawCorpseFixture();
            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(statusMessage, statusType);
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginDisabledGroup(activeProcess != null);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70f)))
            {
                StartCommand("Refreshing local item development data", "--dev-items-list");
            }

            EditorGUI.EndDisabledGroup();
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                activeProcess == null
                    ? "Local Development"
                    : activeProcess.IsBuilding
                        ? "Building GameService..."
                        : "GameService command running...",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawCharacterSelection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Character", EditorStyles.boldLabel);
            var characters = response.data.characters ?? Array.Empty<DevelopmentItemToolCharacter>();
            if (characters.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No initialized local characters were found. Create a character through the "
                        + "normal client flow, then refresh this window.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            characterIndex = Mathf.Clamp(characterIndex, 0, characters.Length - 1);
            var labels = characters.Select(BuildCharacterLabel).ToArray();
            var nextCharacterIndex = EditorGUILayout.Popup("Target", characterIndex, labels);
            if (nextCharacterIndex != characterIndex)
            {
                characterIndex = nextCharacterIndex;
                selectedCharacterId = characters[characterIndex].characterId;
            }

            var character = characters[characterIndex];
            EditorGUILayout.LabelField("Character Id", character.characterId);
            EditorGUILayout.LabelField(
                "Item State",
                "revision " + character.itemStateRevision
                    + ", weight " + character.carriedWeight + " / " + character.carryCapacity);
            EditorGUILayout.LabelField(
                "Stored State",
                character.itemCount + " item instances, "
                    + character.recoveryDeliveryCount + " Recovery deliveries");
            if (character.isOnline)
            {
                EditorGUILayout.HelpBox(
                    "This character has an active simulation session. Leave the world before granting items.",
                    MessageType.Error);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawCustomGrant()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Give Individual Item", EditorStyles.boldLabel);
            var definitions = response.data.definitions ?? Array.Empty<DevelopmentItemToolDefinition>();
            var character = SelectedCharacter();
            if (definitions.Length == 0 || character == null)
            {
                EditorGUILayout.HelpBox(
                    "A character and at least one active item definition are required.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            definitionIndex = Mathf.Clamp(definitionIndex, 0, definitions.Length - 1);
            var definitionLabels = definitions.Select(definition =>
                definition.displayName + " (" + definition.definitionId + ")").ToArray();
            definitionIndex = EditorGUILayout.Popup("Item", definitionIndex, definitionLabels);
            var definition = definitions[definitionIndex];
            quantity = EditorGUILayout.IntSlider(
                "Quantity",
                Mathf.Clamp(quantity, 1, definition.maximumStackSize),
                1,
                definition.maximumStackSize);
            destinationIndex = EditorGUILayout.Popup(
                "Destination",
                destinationIndex,
                DestinationLabels);
            EditorGUILayout.LabelField(
                "Definition",
                definition.categoryId + ", unit weight " + definition.unitWeight
                    + ", max stack " + definition.maximumStackSize);
            var canGrant = activeProcess == null && !character.isOnline;
            EditorGUI.BeginDisabledGroup(!canGrant);
            if (GUILayout.Button("Give Item", GUILayout.Height(30f)))
            {
                StartCommand(
                    "Granting " + definition.definitionId,
                    "--dev-items-grant",
                    character.characterId,
                    definition.definitionId,
                    quantity.ToString(),
                    DestinationArguments[destinationIndex]);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawPackageGrant()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Give Test Package", EditorStyles.boldLabel);
            var packages = response.data.packages ?? Array.Empty<DevelopmentItemToolPackage>();
            var character = SelectedCharacter();
            if (packages.Length == 0 || character == null)
            {
                EditorGUILayout.HelpBox(
                    "A character and at least one development package are required.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            packageIndex = Mathf.Clamp(packageIndex, 0, packages.Length - 1);
            var packageLabels = packages.Select(package => package.displayName).ToArray();
            packageIndex = EditorGUILayout.Popup("Package", packageIndex, packageLabels);
            var selectedPackage = packages[packageIndex];
            EditorGUILayout.HelpBox(selectedPackage.description, MessageType.None);
            var characterIsEmpty = character.itemCount == 0
                && character.recoveryDeliveryCount == 0;
            if (selectedPackage.requiresEmptyCharacter && !characterIsEmpty)
            {
                EditorGUILayout.HelpBox(
                    "Deterministic packages require a character with no items or Recovery deliveries. "
                        + "Individual grants remain available for this offline character.",
                    MessageType.Warning);
            }

            var canGrant = activeProcess == null
                && !character.isOnline
                && (!selectedPackage.requiresEmptyCharacter || characterIsEmpty);
            EditorGUI.BeginDisabledGroup(!canGrant);
            if (GUILayout.Button("Give Package", GUILayout.Height(30f)))
            {
                StartCommand(
                    "Applying " + selectedPackage.displayName,
                    "--dev-items-package",
                    character.characterId,
                    selectedPackage.packageId);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        // The grant goes through the character wallet, so it writes a development ledger row.
        private void DrawCurrencyGrant()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Give Currency", EditorStyles.boldLabel);
            var character = SelectedCharacter();
            if (character == null)
            {
                EditorGUILayout.HelpBox(
                    "An initialized target character is required.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField(
                "Balance",
                character.currency.ToString("N0", CultureInfo.InvariantCulture));
            currencyAmount = EditorGUILayout.LongField("Amount", currencyAmount);
            var canGrant = activeProcess == null
                && !character.isOnline
                && currencyAmount > 0;
            EditorGUI.BeginDisabledGroup(!canGrant);
            if (GUILayout.Button("Give Currency", GUILayout.Height(30f)))
            {
                StartCommand(
                    "Granting currency to " + character.characterName,
                    "--dev-items-currency",
                    character.characterId,
                    currencyAmount.ToString(CultureInfo.InvariantCulture));
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawCorpseFixture()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Create Durable Corpse", EditorStyles.boldLabel);
            var character = SelectedCharacter();
            if (character == null)
            {
                EditorGUILayout.HelpBox(
                    "An initialized source character is required.",
                    MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            corpseShardId = EditorGUILayout.TextField("Shard Id", corpseShardId).Trim();
            corpsePosition = EditorGUILayout.Vector3Field("World Position", corpsePosition);
            EditorGUILayout.HelpBox(
                "The command uses the selected offline character as the death source. "
                    + "If it has no items or Recovery deliveries, the corpse seed package from content/development/item-packages.json is applied first. "
                    + "Restart SimulationWorker after creation so it restores the durable corpse.",
                MessageType.Info);
            var emptyWithRecovery = character.itemCount == 0
                && character.recoveryDeliveryCount != 0;
            if (emptyWithRecovery)
            {
                EditorGUILayout.HelpBox(
                    "This empty character has pending Recovery deliveries. Claim or clear them before auto-seeding the corpse fixture.",
                    MessageType.Warning);
            }

            var canCreate = activeProcess == null
                && !character.isOnline
                && !emptyWithRecovery
                && IsIdentifier(corpseShardId)
                && IsFinite(corpsePosition);
            EditorGUI.BeginDisabledGroup(!canCreate);
            if (GUILayout.Button("Create Durable Corpse", GUILayout.Height(30f)))
            {
                StartCommand(
                    "Creating a durable corpse for " + character.characterName,
                    "--dev-items-corpse",
                    character.characterId,
                    corpseShardId,
                    corpsePosition.x.ToString("R", CultureInfo.InvariantCulture),
                    corpsePosition.y.ToString("R", CultureInfo.InvariantCulture),
                    corpsePosition.z.ToString("R", CultureInfo.InvariantCulture));
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private static bool IsIdentifier(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length <= 128
                && value.All(character =>
                    (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '-'
                    || character == '_');
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x)
                && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y)
                && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z)
                && !float.IsInfinity(value.z)
                && Mathf.Abs(value.x) <= 1_000_000f
                && Mathf.Abs(value.y) <= 1_000_000f
                && Mathf.Abs(value.z) <= 1_000_000f;
        }

        private void RunInitialRefresh()
        {
            initialRefreshScheduled = false;
            if (this != null && activeProcess == null && response == null)
            {
                StartCommand("Refreshing local item development data", "--dev-items-list");
            }
        }

        private void StartCommand(string status, params string[] commandArguments)
        {
            if (activeProcess != null)
            {
                return;
            }

            try
            {
                var repositoryRoot = RepositoryLayout.FindRoot();
                selectedCharacterId = SelectedCharacter()?.characterId ?? selectedCharacterId;
                activeProcess = DevelopmentItemToolCliProcess.Start(
                    repositoryRoot,
                    commandArguments);
                activeCommandStatus = status;
                activeProcessBuilding = true;
                SetStatus(
                    "Building GameService (" + DevelopmentItemToolCli.BuildConfiguration
                        + ") before the command...",
                    MessageType.Info);
            }
            catch (Exception exception)
            {
                SetStatus(
                    "Could not start the GameService build: " + exception.Message,
                    MessageType.Error);
            }
        }

        private void PollActiveProcess()
        {
            if (activeProcess == null)
            {
                return;
            }

            if (!activeProcess.TryComplete(out var completedResponse, out var error))
            {
                if (activeProcessBuilding && !activeProcess.IsBuilding)
                {
                    activeProcessBuilding = false;
                    SetStatus(activeCommandStatus + "...", MessageType.Info);
                }

                return;
            }

            var failureOutput = activeProcess.FailureOutput;
            activeProcess.Dispose();
            activeProcess = null;
            if (completedResponse == null)
            {
                if (!string.IsNullOrWhiteSpace(failureOutput))
                {
                    Debug.LogError("Inventory Item Grants: " + error + "\n\n" + failureOutput);
                    error += "\nThe full output is in the Unity Console.";
                }

                SetStatus(error, MessageType.Error);
                return;
            }

            response = completedResponse;
            if (response.success && response.data != null)
            {
                SynchronizeSelections();
                SetStatus(response.message, MessageType.Info);
            }
            else
            {
                SetStatus(response.message, MessageType.Error);
            }
        }

        private void SynchronizeSelections()
        {
            var characters = response.data.characters ?? Array.Empty<DevelopmentItemToolCharacter>();
            if (characters.Length == 0)
            {
                characterIndex = 0;
                selectedCharacterId = string.Empty;
                return;
            }

            var matchingIndex = Array.FindIndex(characters, character => string.Equals(
                character.characterId,
                selectedCharacterId,
                StringComparison.OrdinalIgnoreCase));
            characterIndex = matchingIndex >= 0
                ? matchingIndex
                : Mathf.Clamp(characterIndex, 0, characters.Length - 1);
            selectedCharacterId = characters[characterIndex].characterId;
            definitionIndex = Mathf.Clamp(
                definitionIndex,
                0,
                Math.Max(0, (response.data.definitions?.Length ?? 1) - 1));
            packageIndex = Mathf.Clamp(
                packageIndex,
                0,
                Math.Max(0, (response.data.packages?.Length ?? 1) - 1));
        }

        private DevelopmentItemToolCharacter SelectedCharacter()
        {
            var characters = response?.data?.characters;
            if (characters == null || characters.Length == 0)
            {
                return null;
            }

            characterIndex = Mathf.Clamp(characterIndex, 0, characters.Length - 1);
            return characters[characterIndex];
        }

        private static string BuildCharacterLabel(DevelopmentItemToolCharacter character)
        {
            var availability = character.isOnline ? "ONLINE" : "offline";
            return character.characterName + " (" + character.accountUsername + ") | "
                + availability + " | " + character.itemCount + " items | "
                + character.carriedWeight + "/" + character.carryCapacity;
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = string.IsNullOrWhiteSpace(message)
                ? "The development item command did not return a message."
                : message;
            statusType = type;
            Repaint();
        }
    }

    public static class DevelopmentItemToolCli
    {
        public const string ResponsePrefix = "MMORPG_DEV_ITEMS_JSON:";
        internal const string BuildConfiguration = "Release";

        private const string UnhandledExceptionPrefix = "Unhandled exception.";
        private const int MaximumReportedBuildLines = 8;

        // Incremental build of GameService and its project references. It writes the built
        // assembly path to targetPathFile, so the command runs exactly that assembly.
        internal static DevelopmentToolProcessStartInfo CreateBuildStartInfo(
            string repositoryRoot,
            string targetPathFile)
        {
            return new DevelopmentToolProcessStartInfo(
                "dotnet",
                new[]
                {
                    "build",
                    RepositoryLayout.GameServiceProjectPath(repositoryRoot),
                    "--configuration",
                    BuildConfiguration,
                    // No build servers and one in-process node, so no process outlives
                    // the build and a cancel ends all of it.
                    "--disable-build-servers",
                    "-m:1",
                    // -getProperty alone only evaluates the project. The explicit target
                    // makes MSBuild build before it reports TargetPath.
                    "-t:Build",
                    "-getProperty:TargetPath",
                    "-getResultOutputFile:" + targetPathFile,
                    "-nologo",
                    "-tl:off",
                    "-v:q"
                },
                repositoryRoot,
                new Dictionary<string, string>
                {
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en"
                });
        }

        internal static DevelopmentToolProcessStartInfo CreateCommandStartInfo(
            string repositoryRoot,
            string assemblyPath,
            IReadOnlyList<string> commandArguments)
        {
            // The built assembly runs inside this one dotnet process. 'dotnet run' would
            // start GameService as a child that survives a cancel or timeout.
            var arguments = new List<string> { assemblyPath };
            arguments.AddRange(commandArguments);
            return new DevelopmentToolProcessStartInfo(
                "dotnet",
                arguments,
                repositoryRoot,
                new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["DOTNET_NOLOGO"] = "1"
                });
        }

        internal static string DescribeBuildFailure(int exitCode, string buildOutput)
        {
            var lines = ReadLines(buildOutput).ToList();
            var errors = lines
                .Where(line => line.IndexOf(": error ", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(RemoveProjectSuffix)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var reported = errors.Count > 0
                ? errors
                : lines.Where(line => !line.StartsWith("Time Elapsed", StringComparison.Ordinal))
                    .ToList();
            var builder = new StringBuilder(
                "GameService could not be built, so the tool did not run an older build. "
                    + "dotnet build exited with code "
                    + exitCode.ToString(CultureInfo.InvariantCulture)
                    + ".");
            foreach (var line in reported.Take(MaximumReportedBuildLines))
            {
                builder.Append('\n').Append(line);
            }

            if (reported.Count > MaximumReportedBuildLines)
            {
                builder.Append('\n')
                    .Append("... and ")
                    .Append((reported.Count - MaximumReportedBuildLines).ToString(CultureInfo.InvariantCulture))
                    .Append(errors.Count > 0 ? " more errors." : " more lines.");
            }

            return builder.ToString();
        }

        internal static string DescribeStartupFailure(
            int exitCode,
            string standardOutput,
            string standardError)
        {
            var exceptionLine = FindUnhandledException(standardError)
                ?? FindUnhandledException(standardOutput)
                ?? ReadLines(standardError).FirstOrDefault(line => !IsStackTraceLine(line));
            var builder = new StringBuilder(
                "GameService failed before it returned a development item response");
            builder.Append(exceptionLine == null ? "." : ": " + exceptionLine);
            builder.Append('\n')
                .Append("GameService exited with code ")
                .Append(exitCode.ToString(CultureInfo.InvariantCulture))
                .Append('.');
            return builder.ToString();
        }

        // Reads the response of a finished GameService command. Without a response line,
        // the error describes why GameService stopped first.
        internal static bool TryReadCommandResult(
            int exitCode,
            string standardOutput,
            string standardError,
            out DevelopmentItemToolResponse response,
            out string error)
        {
            if (FindResponseLine(standardOutput) != null)
            {
                return TryParseResponse(standardOutput, out response, out error);
            }

            response = null;
            error = DescribeStartupFailure(exitCode, standardOutput, standardError);
            return false;
        }

        public static bool TryParseResponse(
            string output,
            out DevelopmentItemToolResponse response,
            out string error)
        {
            response = null;
            error = string.Empty;
            var responseLine = FindResponseLine(output);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "GameService did not return a development item response.";
                return false;
            }

            try
            {
                response = JsonUtility.FromJson<DevelopmentItemToolResponse>(
                    responseLine.Substring(ResponsePrefix.Length));
                if (response == null || string.IsNullOrWhiteSpace(response.message))
                {
                    response = null;
                    error = "GameService returned an invalid development item response.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Could not parse the GameService development item response: "
                    + exception.Message;
                return false;
            }
        }

        private static string FindResponseLine(string output)
        {
            return (output ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .LastOrDefault(line => line.StartsWith(ResponsePrefix, StringComparison.Ordinal));
        }

        // .NET reports an unhandled exception as one 'Unhandled exception. <type>: <message>'
        // line before the stack trace.
        private static string FindUnhandledException(string output)
        {
            var line = ReadLines(output).FirstOrDefault(candidate =>
                candidate.StartsWith(UnhandledExceptionPrefix, StringComparison.Ordinal));
            if (line == null)
            {
                return null;
            }

            var exception = line.Substring(UnhandledExceptionPrefix.Length).Trim();
            return exception.Length == 0 ? line : exception;
        }

        private static bool IsStackTraceLine(string line)
        {
            return line.StartsWith("at ", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal);
        }

        // MSBuild ends each message with the project path in brackets.
        private static string RemoveProjectSuffix(string line)
        {
            var suffixStart = line.LastIndexOf(" [", StringComparison.Ordinal);
            return suffixStart > 0
                && line.EndsWith("proj]", StringComparison.OrdinalIgnoreCase)
                    ? line.Substring(0, suffixStart)
                    : line;
        }

        // Non-empty trimmed lines.
        private static IEnumerable<string> ReadLines(string output)
        {
            return (output ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);
        }
    }

    // Builds GameService, then runs one development item command with exactly the
    // assembly that build produced. A failed build never falls back to an older one.
    internal sealed class DevelopmentItemToolCliProcess : IDisposable
    {
        internal static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

        private readonly string repositoryRoot;
        private readonly string[] commandArguments;
        private readonly Func<DevelopmentToolProcessStartInfo, TimeSpan, IDevelopmentToolProcess> startProcess;
        private readonly string targetPathFile;
        private IDevelopmentToolProcess activeProcess;
        private bool completed;

        private DevelopmentItemToolCliProcess(
            string repositoryRoot,
            IReadOnlyList<string> commandArguments,
            Func<DevelopmentToolProcessStartInfo, TimeSpan, IDevelopmentToolProcess> startProcess)
        {
            this.repositoryRoot = repositoryRoot
                ?? throw new ArgumentNullException(nameof(repositoryRoot));
            this.commandArguments = (commandArguments
                ?? throw new ArgumentNullException(nameof(commandArguments))).ToArray();
            this.startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
            targetPathFile = Path.Combine(
                Path.GetTempPath(),
                "mmorpg-dev-items-target-" + Guid.NewGuid().ToString("N") + ".txt");
        }

        public bool IsBuilding { get; private set; }

        // Complete output of the step that failed, for the Unity Console.
        public string FailureOutput { get; private set; } = string.Empty;

        public static DevelopmentItemToolCliProcess Start(
            string repositoryRoot,
            IReadOnlyList<string> commandArguments)
        {
            return Start(repositoryRoot, commandArguments, DevelopmentToolProcess.Start);
        }

        internal static DevelopmentItemToolCliProcess Start(
            string repositoryRoot,
            IReadOnlyList<string> commandArguments,
            Func<DevelopmentToolProcessStartInfo, TimeSpan, IDevelopmentToolProcess> startProcess)
        {
            var operation = new DevelopmentItemToolCliProcess(
                repositoryRoot,
                commandArguments,
                startProcess);
            try
            {
                operation.activeProcess = startProcess(
                    DevelopmentItemToolCli.CreateBuildStartInfo(
                        operation.repositoryRoot,
                        operation.targetPathFile),
                    BuildTimeout);
            }
            catch
            {
                operation.Dispose();
                throw;
            }

            operation.IsBuilding = true;
            return operation;
        }

        public bool TryComplete(
            out DevelopmentItemToolResponse response,
            out string error)
        {
            response = null;
            error = string.Empty;
            if (completed)
            {
                error = "The GameService development item command has already completed.";
                return true;
            }

            if (!activeProcess.TryGetResult(out var result))
            {
                return false;
            }

            activeProcess.Dispose();
            activeProcess = null;
            if (IsBuilding)
            {
                IsBuilding = false;
                if (TryStartCommand(result, out error))
                {
                    return false;
                }

                completed = true;
                return true;
            }

            completed = true;
            if (result.TimedOut)
            {
                error = "The GameService development item command timed out after "
                    + CommandTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                    + " seconds.";
                FailureOutput = CombineOutput(result);
                return true;
            }

            if (!DevelopmentItemToolCli.TryReadCommandResult(
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                    out response,
                    out error))
            {
                FailureOutput = CombineOutput(result);
            }

            return true;
        }

        public void Dispose()
        {
            activeProcess?.Dispose();
            activeProcess = null;
            DeleteTargetPathFile();
        }

        private bool TryStartCommand(DevelopmentToolProcessResult build, out string error)
        {
            if (build.TimedOut || build.ExitCode != 0)
            {
                error = build.TimedOut
                    ? "The GameService build timed out after "
                        + BuildTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                        + " minutes, so the tool did not run an older build."
                    : DevelopmentItemToolCli.DescribeBuildFailure(
                        build.ExitCode,
                        CombineOutput(build));
                FailureOutput = CombineOutput(build);
                return false;
            }

            var assemblyPath = ReadTargetPath();
            if (assemblyPath == null)
            {
                error = "The GameService build succeeded but did not report its assembly path.";
                FailureOutput = CombineOutput(build);
                return false;
            }

            if (!File.Exists(assemblyPath))
            {
                error = "The GameService build reported '" + assemblyPath
                    + "', but that file does not exist.";
                return false;
            }

            try
            {
                activeProcess = startProcess(
                    DevelopmentItemToolCli.CreateCommandStartInfo(
                        repositoryRoot,
                        assemblyPath,
                        commandArguments),
                    CommandTimeout);
            }
            catch (Exception exception)
            {
                error = "Could not start GameService: " + exception.Message;
                return false;
            }

            error = string.Empty;
            return true;
        }

        private string ReadTargetPath()
        {
            try
            {
                if (!File.Exists(targetPathFile))
                {
                    return null;
                }

                return File.ReadAllLines(targetPathFile)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 0);
            }
            finally
            {
                DeleteTargetPathFile();
            }
        }

        private void DeleteTargetPathFile()
        {
            try
            {
                File.Delete(targetPathFile);
            }
            catch (IOException)
            {
                // A leftover file in the temporary directory is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover file in the temporary directory is harmless.
            }
        }

        private static string CombineOutput(DevelopmentToolProcessResult result)
        {
            var output = result.StandardOutput.Trim();
            var error = result.StandardError.Trim();
            if (output.Length == 0)
            {
                return error;
            }

            return error.Length == 0 ? output : error + "\n\n" + output;
        }
    }

    [Serializable]
    public sealed class DevelopmentItemToolResponse
    {
        public bool success;
        public string message;
        public DevelopmentItemToolData data;
    }

    [Serializable]
    public sealed class DevelopmentItemToolData
    {
        public DevelopmentItemToolCharacter[] characters;
        public DevelopmentItemToolDefinition[] definitions;
        public DevelopmentItemToolPackage[] packages;
    }

    [Serializable]
    public sealed class DevelopmentItemToolCharacter
    {
        public string characterId;
        public string characterName;
        public string accountUsername;
        public bool isOnline;
        public int itemCount;
        public int recoveryDeliveryCount;
        public long itemStateRevision;
        public long carriedWeight;
        public long carryCapacity;
        public long currency;
    }

    [Serializable]
    public sealed class DevelopmentItemToolDefinition
    {
        public string definitionId;
        public string displayName;
        public string categoryId;
        public int maximumStackSize;
        public long unitWeight;
    }

    [Serializable]
    public sealed class DevelopmentItemToolPackage
    {
        public string packageId;
        public string displayName;
        public string description;
        public bool requiresEmptyCharacter;
    }
}
