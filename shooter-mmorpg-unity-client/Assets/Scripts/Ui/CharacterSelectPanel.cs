using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Diagnostics;
using ShooterMmo.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class CharacterSelectPanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private ShooterMmoApiClient apiClient;
        private string characterName = "Hero One";
        private string status = "Select or create a character, then join a shard.";
        private CharacterResponse[] characters = new CharacterResponse[0];
        private ShardResponse[] shards = new ShardResponse[0];
        private int selectedCharacterIndex;
        private int selectedShardIndex;
        private bool operationStepFailed;
        private Vector2 scrollPosition;

        private void Awake()
        {
            apiClient = new ShooterMmoApiClient(ShooterMmoClientSession.RequestTimeoutSeconds);
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
            GUILayout.Label(ShooterMmoClientSession.Auth != null
                ? "Signed in: " + ShooterMmoClientSession.Auth.username
                : "Not signed in.");

            if (GUILayout.Button("Back To Login", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.Logout, LogoutRoutine());
            }

            GUILayout.Space(12f);
        }

        private IEnumerator LogoutRoutine()
        {
            var account = ShooterMmoClientSession.Auth;
            var accountName = account != null ? account.username : "unknown";
            var accountId = account != null ? account.accountId : "unknown";
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Logging out account '" + accountName + "' (" + accountId + ").");

            ShooterMmoApiError error = null;
            yield return apiClient.Logout(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
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
            ShooterMmoClientSession.Clear();
            SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
        }

        private void DrawCharacterSection()
        {
            GUILayout.Label("Characters");
            characterName = TemporaryPanelStyles.LabeledTextField("Name", characterName);

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
                    labels[index] = characters[index].name;
                }

                selectedCharacterIndex = GUILayout.SelectionGrid(selectedCharacterIndex, labels, 1);
                ShooterMmoClientSession.SelectedCharacter = characters[selectedCharacterIndex];
            }
            else
            {
                GUILayout.Label("No characters loaded.");
            }

            GUILayout.Space(12f);
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
                ShooterMmoClientSession.SelectedShard = shards[selectedShardIndex];
            }
            else
            {
                GUILayout.Label("No shards loaded.");
            }
        }

        private IEnumerator LoadSelectionRoutine()
        {
            yield return RefreshCharactersRoutine();
            if (operationStepFailed || !ShooterMmoClientSession.IsAuthenticated)
            {
                yield break;
            }

            yield return RefreshShardsRoutine();
            status = "Character and shard data loaded.";
        }

        private IEnumerator CreateCharacterRoutine()
        {
            if (!EnsureAuthenticated())
            {
                yield break;
            }

            CharacterResponse createdCharacter = null;
            ShooterMmoApiError error = null;
            var request = new CreateCharacterRequest { name = characterName };

            yield return apiClient.CreateCharacter(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
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

        private IEnumerator RefreshCharactersRoutine()
        {
            if (!EnsureAuthenticated())
            {
                yield break;
            }

            CharacterResponse[] result = null;
            ShooterMmoApiError error = null;
            yield return apiClient.GetCharacters(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            characters = result ?? new CharacterResponse[0];
            selectedCharacterIndex = Mathf.Clamp(selectedCharacterIndex, 0, Mathf.Max(0, characters.Length - 1));
            ShooterMmoClientSession.SelectedCharacter = characters.Length > 0
                ? characters[selectedCharacterIndex]
                : null;
            status = "Loaded " + characters.Length + " characters.";
        }

        private IEnumerator RefreshShardsRoutine()
        {
            ShardResponse[] result = null;
            ShooterMmoApiError error = null;
            yield return apiClient.GetShards(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            shards = result ?? new ShardResponse[0];
            selectedShardIndex = Mathf.Clamp(selectedShardIndex, 0, Mathf.Max(0, shards.Length - 1));
            ShooterMmoClientSession.SelectedShard = shards.Length > 0
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
            ShooterMmoClientSession.SelectedCharacter = character;
            ShooterMmoClientSession.SelectedShard = shard;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Character '" + character.name + "' (" + character.id + ") is requesting access to shard '"
                + shard.id + "'.");

            JoinShardResponse joinTicket = null;
            ShooterMmoApiError error = null;
            yield return apiClient.CreateJoinTicket(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                shard.id,
                new JoinShardRequest { characterId = character.id },
                result => joinTicket = result,
                result => error = result);

            if (HandleError(error))
            {
                yield break;
            }

            ClientLog.Info(
                ClientLogCategory.Auth,
                "AuthService issued a short-lived join ticket for character '" + character.name
                + "' on shard '" + shard.id + "'.");

            var simulationClient = ShooterMmoClientBootstrap.SimulationClient;
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

            ShooterMmoClientSession.ActiveSimulationSession = activeSession;
            status = "Joined shard " + activeSession.shardId + " as " + activeSession.characterName + ".";
            SceneManager.LoadScene(ShooterMmoSceneNames.WorldScene);
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
            if (ShooterMmoClientSession.IsAuthenticated)
            {
                return true;
            }

            ShooterMmoClientSession.Clear();
            SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
            return false;
        }

        private bool HandleError(ShooterMmoApiError error)
        {
            if (error == null)
            {
                return false;
            }

            operationStepFailed = true;

            if (!ClientSessionRecovery.ReturnToLoginIfUnauthorized(error))
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
