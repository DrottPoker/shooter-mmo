using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Ui
{
    public sealed class CharacterSelectPanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private ApiClient apiClient;
        private RealtimeSimulationClient simulationClient;
        private string characterName = "Hero One";
        private CharacterCreationLook creationLook;
        private string status = "Select or create a character, then join a shard.";
        private CharacterResponse[] characters = new CharacterResponse[0];
        private ShardResponse[] shards = new ShardResponse[0];
        private int selectedCharacterIndex;
        private int selectedShardIndex;
        private bool operationStepFailed;
        private Vector2 scrollPosition;

        // Called by the composition root before the component is activated.
        public void Initialize(RealtimeSimulationClient realtimeClient)
        {
            simulationClient = realtimeClient;
        }

        private void OnDestroy()
        {
            apiClient?.Dispose();
        }

        private void Awake()
        {
            apiClient = new ApiClient(ClientSession.RequestTimeoutSeconds);
        }

        private void Start()
        {
            if (!EnsureAuthenticated())
            {
                return;
            }

            BeginOperation(ClientOperation.LoadSelection, LoadSelectionRoutine());
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(620f, 720f), "Character Select", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            var previousGuiState = GUI.enabled;
            GUI.enabled = previousGuiState && !operationState.IsBusy;
            DrawAccountSection();
            DrawCharacterSection();
            DrawShardSection();
            GUI.enabled = previousGuiState;

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(operationState.IsBusy, status);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawAccountSection()
        {
            GUILayout.Label("Account");
            GUILayout.Label(ClientSession.Auth != null
                ? "Signed in: " + ClientSession.Auth.username
                : "Not signed in.");

            if (GUILayout.Button("Back To Login", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.Logout, LogoutRoutine());
            }

            GUILayout.Space(12f);
        }

        private IEnumerator LogoutRoutine()
        {
            var account = ClientSession.Auth;
            var accountName = account != null ? account.username : "unknown";
            var accountId = account != null ? account.accountId : "unknown";
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Logging out account '" + accountName + "' (" + accountId + ").");

            ApiError error = null;
            yield return apiClient.Logout(
                ClientSession.GameServiceBaseUrl,
                ClientSession.SessionToken,
                () => { },
                value => error = value);

            if (error != null && !error.IsUnauthorized)
            {
                HandleError(error);
                yield break;
            }

            ClientLog.Info(
                ClientLogCategory.Auth,
                "Account '" + accountName + "' logged out and its current session was revoked.");
            ClientSession.Clear();
            ClientSceneLoader.LoadLoginMenu();
        }

        private void DrawCharacterSection()
        {
            GUILayout.Label("Characters");
            characterName = TemporaryPanelStyles.LabeledTextField("Name", characterName);
            DrawCreationLook();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create Character", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.CreateCharacter, CreateCharacterRoutine());
            }

            if (GUILayout.Button("Refresh Characters", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.RefreshCharacters, RefreshCharactersRoutine());
            }
            GUILayout.EndHorizontal();

            if (characters.Length > 0)
            {
                selectedCharacterIndex = Mathf.Clamp(selectedCharacterIndex, 0, characters.Length - 1);
                var labels = new string[characters.Length];
                for (var index = 0; index < characters.Length; index++)
                {
                    labels[index] = characters[index].name
                        + (string.IsNullOrEmpty(characters[index].bodyModel) ? string.Empty : " (" + characters[index].bodyModel + ")");
                }

                selectedCharacterIndex = GUILayout.SelectionGrid(selectedCharacterIndex, labels, 1);
                ClientSession.SelectedCharacter = characters[selectedCharacterIndex];
            }
            else
            {
                GUILayout.Label("No characters loaded.");
            }

            GUILayout.Space(12f);
        }

        // Temporary rows that step through the body models and the options of every kind of look
        // the body offers.
        private void DrawCreationLook()
        {
            if (creationLook == null || !creationLook.IsAvailable)
            {
                return;
            }

            creationLook.StepBodyModel(StepRow("Body", creationLook.BodyModel));
            for (var offer = 0; offer < creationLook.OfferCount; offer++)
            {
                creationLook.StepOption(offer, StepRow(creationLook.OfferLabel(offer), creationLook.OptionLabel(offer)));
            }
        }

        // A row with a label and buttons that step back and forth: -1, 1, or 0 when neither was pressed.
        private static int StepRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": " + value, GUILayout.Width(360f));
            var step = GUILayout.Button("<", GUILayout.Width(40f)) ? -1 : 0;
            step = GUILayout.Button(">", GUILayout.Width(40f)) ? 1 : step;
            GUILayout.EndHorizontal();
            return step;
        }

        private void DrawShardSection()
        {
            GUILayout.Label("Shards");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Shards", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.RefreshShards, RefreshShardsRoutine());
            }

            if (GUILayout.Button("Join Selected Shard", GUILayout.Height(32f)))
            {
                BeginJoin();
            }
            GUILayout.EndHorizontal();

            if (shards.Length > 0)
            {
                selectedShardIndex = Mathf.Clamp(selectedShardIndex, 0, shards.Length - 1);
                var labels = new string[shards.Length];
                for (var index = 0; index < shards.Length; index++)
                {
                    var shard = shards[index];
                    labels[index] = shard.displayName
                        + " (" + shard.regionCode + ", " + shard.fleetDisplayName + ") "
                        + (shard.isOnline
                            ? "Online " + shard.activePlayers + "/" + shard.capacity
                            : "Offline");
                }

                selectedShardIndex = GUILayout.SelectionGrid(selectedShardIndex, labels, 1);
                ClientSession.SelectedShard = shards[selectedShardIndex];
            }
            else
            {
                GUILayout.Label("No shards loaded.");
            }
        }

        private IEnumerator LoadSelectionRoutine()
        {
            yield return RefreshCharactersRoutine();
            if (operationStepFailed || !ClientSession.IsAuthenticated)
            {
                yield break;
            }

            yield return RefreshShardsRoutine();
            if (operationStepFailed)
            {
                yield break;
            }

            yield return LoadCreationLookRoutine();
            status = "Character and shard data loaded.";
        }

        private IEnumerator CreateCharacterRoutine()
        {
            if (!EnsureAuthenticated())
            {
                yield break;
            }

            // JsonUtility writes a missing appearance as an empty one, which GameService refuses, so
            // a character is always created with the offers loaded.
            if (creationLook == null || !creationLook.IsAvailable)
            {
                yield return LoadCreationLookRoutine();
                if (creationLook == null || !creationLook.IsAvailable)
                {
                    yield break;
                }
            }

            CharacterResponse createdCharacter = null;
            ApiError error = null;
            var request = new CreateCharacterRequest
            {
                name = characterName,
                appearance = creationLook.ToRequest()
            };

            yield return apiClient.CreateCharacter(
                ClientSession.GameServiceBaseUrl,
                ClientSession.SessionToken,
                request,
                result => createdCharacter = result,
                result => error = result);

            if (HandleError(error))
            {
                yield break;
            }

            status = "Created character " + createdCharacter.name + ".";
            yield return RefreshCharactersRoutine();
        }

        // What character creation offers; without it a new character gets the default look.
        private IEnumerator LoadCreationLookRoutine()
        {
            CharacterAppearanceCatalogResponse result = null;
            ApiError error = null;
            yield return apiClient.GetCharacterAppearanceCatalog(
                ClientSession.GameServiceBaseUrl,
                ClientSession.SessionToken,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            creationLook = new CharacterCreationLook(result);
        }

        private IEnumerator RefreshCharactersRoutine()
        {
            if (!EnsureAuthenticated())
            {
                yield break;
            }

            CharacterResponse[] result = null;
            ApiError error = null;
            yield return apiClient.GetCharacters(
                ClientSession.GameServiceBaseUrl,
                ClientSession.SessionToken,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            characters = result ?? new CharacterResponse[0];
            selectedCharacterIndex = Mathf.Clamp(selectedCharacterIndex, 0, Mathf.Max(0, characters.Length - 1));
            ClientSession.SelectedCharacter = characters.Length > 0
                ? characters[selectedCharacterIndex]
                : null;
            status = "Loaded " + characters.Length + " characters.";
        }

        private IEnumerator RefreshShardsRoutine()
        {
            ShardResponse[] result = null;
            ApiError error = null;
            yield return apiClient.GetShards(
                ClientSession.GameServiceBaseUrl,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            shards = result ?? new ShardResponse[0];
            selectedShardIndex = Mathf.Clamp(selectedShardIndex, 0, Mathf.Max(0, shards.Length - 1));
            ClientSession.SelectedShard = shards.Length > 0
                ? shards[selectedShardIndex]
                : null;
            status = "Loaded " + shards.Length + " shards.";
        }

        private void BeginJoin()
        {
            if (!EnsureAuthenticated() || characters.Length == 0 || shards.Length == 0)
            {
                status = "Load a character and a shard before joining.";
                return;
            }

            var shard = shards[selectedShardIndex];
            if (!shard.isOnline)
            {
                status = shard.displayName + " is offline.";
                return;
            }

            BeginOperation(ClientOperation.JoinShard, JoinSelectedShardRoutine());
        }

        private IEnumerator JoinSelectedShardRoutine()
        {
            var character = characters[selectedCharacterIndex];
            var shard = shards[selectedShardIndex];
            if (!WorldSceneCatalog.TryResolveScene(
                    shard.worldId,
                    out var worldSceneName,
                    out var worldSceneError))
            {
                status = "Cannot join " + shard.displayName + ": " + worldSceneError;
                ClientLog.Error(ClientLogCategory.Client, status);
                yield break;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                status = "Cannot join " + shard.displayName + ": scene '" + worldSceneName
                    + "' is not an Addressables World scene in this client build.";
                ClientLog.Error(ClientLogCategory.Client, status);
                yield break;
            }

            ClientSession.SelectedCharacter = character;
            ClientSession.SelectedShard = shard;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Character '" + character.name + "' (" + character.id + ") is requesting access to shard '"
                + shard.id + "'.");

            JoinShardResponse joinTicket = null;
            ApiError error = null;
            yield return apiClient.CreateJoinTicket(
                ClientSession.GameServiceBaseUrl,
                ClientSession.SessionToken,
                shard.id,
                new JoinShardRequest { characterId = character.id },
                result => joinTicket = result,
                result => error = result);

            if (HandleError(error))
            {
                yield break;
            }

            if (joinTicket == null || joinTicket.shard == null)
            {
                status = "Cannot join " + shard.displayName
                    + ": GameService returned incomplete placement data.";
                ClientLog.Error(ClientLogCategory.Client, status);
                yield break;
            }

            if (!WorldSceneCatalog.TryResolveScene(
                    joinTicket.shard.worldId,
                    out worldSceneName,
                    out worldSceneError))
            {
                status = "Cannot join " + shard.displayName + ": " + worldSceneError;
                ClientLog.Error(ClientLogCategory.Client, status);
                yield break;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                status = "Cannot join " + shard.displayName + ": scene '" + worldSceneName
                    + "' is not an Addressables World scene in this client build.";
                ClientLog.Error(ClientLogCategory.Client, status);
                yield break;
            }

            ClientSession.SelectedShard = joinTicket.shard;

            ClientLog.Info(
                ClientLogCategory.Auth,
                "GameService issued a short-lived join ticket for character '" + character.name
                + "' on shard '" + shard.id + "'.");

            if (simulationClient == null)
            {
                ClientLog.Error(
                    ClientLogCategory.Client,
                    "The persistent realtime client is unavailable, so the simulation join cannot start.");
                HandleRealtimeError(new RealtimeClientError(
                    RealtimeClientErrorKind.Network,
                    "realtime_client_missing",
                    "The persistent realtime client is unavailable."));
                yield break;
            }

            ActiveSimulationSessionResponse activeSession = null;
            RealtimeClientError realtimeError = null;
            yield return simulationClient.Join(
                joinTicket,
                result => activeSession = result,
                result => realtimeError = result);

            if (HandleRealtimeError(realtimeError))
            {
                yield break;
            }

            ClientSession.ActiveSimulationSession = activeSession;
            status = "Joined shard " + activeSession.shardId + " as " + activeSession.characterName
                + ". Loading " + worldSceneName + ".";

            // A successful load replaces this scene and ends the routine. A failed load keeps
            // CharacterSelect, so the joined session is released here.
            var sceneLoad = ClientSceneLoader.LoadWorldScene(worldSceneName);
            yield return sceneLoad;
            if (sceneLoad.Status == AsyncOperationStatus.Succeeded)
            {
                yield break;
            }

            var loadError = sceneLoad.OperationException != null
                ? sceneLoad.OperationException.Message
                : "unknown error";
            status = "Cannot enter " + worldSceneName + ": the scene failed to load (" + loadError + ").";
            ClientLog.Error(ClientLogCategory.Client, status);
            RealtimeClientError leaveError = null;
            yield return simulationClient.Leave(
                activeSession.simulationSessionId,
                () => { },
                value => leaveError = value);
            if (leaveError != null)
            {
                simulationClient.Abort();
            }

            ClientSession.ActiveSimulationSession = null;
        }

        private void BeginOperation(ClientOperation operation, IEnumerator routine)
        {
            if (!operationState.TryBegin(operation))
            {
                return;
            }

            operationStepFailed = false;
            StartCoroutine(RunOperation(operation, routine));
        }

        private IEnumerator RunOperation(ClientOperation operation, IEnumerator routine)
        {
            yield return routine;
            operationState.Complete(operation);
        }

        private bool EnsureAuthenticated()
        {
            if (ClientSession.IsAuthenticated)
            {
                return true;
            }

            ClientSession.Clear();
            ClientSceneLoader.LoadLoginMenu();
            return false;
        }

        private bool HandleError(ApiError error)
        {
            if (error == null)
            {
                return false;
            }

            operationStepFailed = true;

            if (!ClientSessionRecovery.ReturnToLoginIfUnauthorized(error, simulationClient))
            {
                status = "Error: " + error.ToDisplayMessage();
                ClientLog.Error(ClientLogCategory.Client, "API request failed: " + error.ToDisplayMessage());
            }

            return true;
        }

        private bool HandleRealtimeError(RealtimeClientError error)
        {
            if (error == null)
            {
                return false;
            }

            operationStepFailed = true;
            status = "Error: " + error.ToDisplayMessage();
            return true;
        }
    }
}
