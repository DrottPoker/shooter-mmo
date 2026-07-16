using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ShooterMmo.Editor
{
    public sealed class InventoryItemGrantWindow : EditorWindow
    {
        public const string MenuPath = "Shooter MMO/Tools/Inventory Item Grants";

        private static readonly string[] DestinationLabels =
        {
            "Permanent Inventory",
            "Bank",
            "Secure Container"
        };

        private static readonly string[] DestinationArguments =
        {
            "permanent",
            "bank",
            "secure"
        };

        private DevelopmentItemToolCliProcess activeProcess;
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
                "This Development-only tool sends grants through AuthService and "
                    + "ItemTransactionService. It never writes directly to PostgreSQL. "
                    + "The selected character must be offline.",
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
                activeProcess == null ? "Local Development" : "AuthService command running...",
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
            if (destinationIndex == 2 && !definition.secureContainerEligible)
            {
                EditorGUILayout.HelpBox(
                    "The canonical catalog does not allow this definition in the Secure Container.",
                    MessageType.Warning);
            }

            var canGrant = activeProcess == null
                && !character.isOnline
                && (destinationIndex != 2 || definition.secureContainerEligible);
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
                var repositoryRoot = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "..", ".."));
                selectedCharacterId = SelectedCharacter()?.characterId ?? selectedCharacterId;
                activeProcess = DevelopmentItemToolCliProcess.Start(
                    repositoryRoot,
                    commandArguments);
                SetStatus(status + "...", MessageType.Info);
            }
            catch (Exception exception)
            {
                SetStatus(
                    "Could not start AuthService: " + exception.Message,
                    MessageType.Error);
            }
        }

        private void PollActiveProcess()
        {
            if (activeProcess == null
                || !activeProcess.TryComplete(out var completedResponse, out var error))
            {
                return;
            }

            activeProcess.Dispose();
            activeProcess = null;
            if (completedResponse == null)
            {
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
        public const string ResponsePrefix = "SHOOTER_MMO_DEV_ITEMS_JSON:";

        public static bool TryParseResponse(
            string output,
            out DevelopmentItemToolResponse response,
            out string error)
        {
            response = null;
            error = string.Empty;
            var responseLine = (output ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .LastOrDefault(line => line.StartsWith(ResponsePrefix, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                error = "AuthService did not return a development item response. "
                    + "Build the Release solution once with 'dotnet build ShooterMmo.slnx "
                    + "--configuration Release', then retry.";
                return false;
            }

            try
            {
                response = JsonUtility.FromJson<DevelopmentItemToolResponse>(
                    responseLine.Substring(ResponsePrefix.Length));
                if (response == null || string.IsNullOrWhiteSpace(response.message))
                {
                    response = null;
                    error = "AuthService returned an invalid development item response.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Could not parse the AuthService development item response: "
                    + exception.Message;
                return false;
            }
        }
    }

    internal sealed class DevelopmentItemToolCliProcess : IDisposable
    {
        private const double TimeoutSeconds = 60d;

        private readonly object outputLock = new object();
        private readonly List<string> standardOutput = new List<string>();
        private readonly List<string> standardError = new List<string>();
        private readonly Process process;
        private readonly double startedAt;
        private bool completed;

        private DevelopmentItemToolCliProcess(Process process)
        {
            this.process = process;
            startedAt = EditorApplication.timeSinceStartup;
            process.OutputDataReceived += CaptureStandardOutput;
            process.ErrorDataReceived += CaptureStandardError;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public static DevelopmentItemToolCliProcess Start(
            string repositoryRoot,
            IReadOnlyList<string> commandArguments)
        {
            var projectPath = Path.Combine(
                repositoryRoot,
                "AuthService",
                "AuthService.csproj");
            if (!File.Exists(projectPath))
            {
                throw new FileNotFoundException(
                    "Could not locate AuthService.csproj from the Unity project.",
                    projectPath);
            }

            var arguments = new List<string>
            {
                "run",
                "--project",
                projectPath,
                "--configuration",
                "Release",
                "--no-build",
                "--"
            };
            arguments.AddRange(commandArguments);
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
            startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";
            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The dotnet process did not start.");
            }

            return new DevelopmentItemToolCliProcess(process);
        }

        public bool TryComplete(
            out DevelopmentItemToolResponse response,
            out string error)
        {
            response = null;
            error = string.Empty;
            if (completed)
            {
                error = "The AuthService development item command has already completed.";
                return true;
            }

            if (!process.HasExited
                && EditorApplication.timeSinceStartup - startedAt <= TimeoutSeconds)
            {
                return false;
            }

            if (!process.HasExited)
            {
                process.Kill();
                completed = true;
                error = "The AuthService development item command timed out after 60 seconds.";
                return true;
            }

            process.WaitForExit();
            completed = true;
            string output;
            string stderr;
            lock (outputLock)
            {
                output = string.Join(Environment.NewLine, standardOutput);
                stderr = string.Join(Environment.NewLine, standardError);
            }

            if (DevelopmentItemToolCli.TryParseResponse(output, out response, out error))
            {
                return true;
            }

            var diagnostic = string.IsNullOrWhiteSpace(stderr) ? output : stderr;
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                error += Environment.NewLine + Environment.NewLine + diagnostic.Trim();
            }

            return true;
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.OutputDataReceived -= CaptureStandardOutput;
            process.ErrorDataReceived -= CaptureStandardError;
            process.Dispose();
        }

        private void CaptureStandardOutput(object sender, DataReceivedEventArgs eventArgs)
        {
            if (eventArgs.Data == null)
            {
                return;
            }

            lock (outputLock)
            {
                standardOutput.Add(eventArgs.Data);
            }
        }

        private void CaptureStandardError(object sender, DataReceivedEventArgs eventArgs)
        {
            if (eventArgs.Data == null)
            {
                return;
            }

            lock (outputLock)
            {
                standardError.Add(eventArgs.Data);
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.All(character => !char.IsWhiteSpace(character) && character != '"'))
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
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
    }

    [Serializable]
    public sealed class DevelopmentItemToolDefinition
    {
        public string definitionId;
        public string displayName;
        public string categoryId;
        public int maximumStackSize;
        public long unitWeight;
        public bool secureContainerEligible;
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
