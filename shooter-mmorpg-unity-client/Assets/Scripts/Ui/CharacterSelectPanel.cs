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
        private string status = "Select or create a character, then join a world.";
        private CharacterResponse[] characters = new CharacterResponse[0];
        private WorldResponse[] worlds = new WorldResponse[0];
        private int selectedCharacterIndex;
        private int selectedWorldIndex;
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
            DrawWorldSection();
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

        private void DrawWorldSection()
        {
            GUILayout.Label("Worlds");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Worlds", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.RefreshWorlds, RefreshWorldsRoutine());
            }

            if (GUILayout.Button("Join Selected World", GUILayout.Height(32f)))
            {
                BeginJoin();
            }
            GUILayout.EndHorizontal();

            if (worlds.Length > 0)
            {
                selectedWorldIndex = Mathf.Clamp(selectedWorldIndex, 0, worlds.Length - 1);
                var labels = new string[worlds.Length];
                for (var index = 0; index < worlds.Length; index++)
                {
                    labels[index] = worlds[index].displayName
                        + " (" + worlds[index].id + ") "
                        + (worlds[index].isOnline ? "Online" : "Offline");
                }

                selectedWorldIndex = GUILayout.SelectionGrid(selectedWorldIndex, labels, 1);
                ShooterMmoClientSession.SelectedWorld = worlds[selectedWorldIndex];
            }
            else
            {
                GUILayout.Label("No worlds loaded.");
            }
        }

        private IEnumerator LoadSelectionRoutine()
        {
            yield return RefreshCharactersRoutine();
            if (operationStepFailed || !ShooterMmoClientSession.IsAuthenticated)
            {
                yield break;
            }

            yield return RefreshWorldsRoutine();
            status = "Character and world data loaded.";
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

        private IEnumerator RefreshWorldsRoutine()
        {
            WorldResponse[] result = null;
            ShooterMmoApiError error = null;
            yield return apiClient.GetWorlds(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                value => result = value,
                value => error = value);

            if (HandleError(error))
            {
                yield break;
            }

            worlds = result ?? new WorldResponse[0];
            selectedWorldIndex = Mathf.Clamp(selectedWorldIndex, 0, Mathf.Max(0, worlds.Length - 1));
            ShooterMmoClientSession.SelectedWorld = worlds.Length > 0
                ? worlds[selectedWorldIndex]
                : null;
            status = "Loaded " + worlds.Length + " worlds.";
        }

        private void BeginJoin()
        {
            if (!EnsureAuthenticated() || characters.Length == 0 || worlds.Length == 0)
            {
                status = "Load a character and a world before joining.";
                return;
            }

            var world = worlds[selectedWorldIndex];
            if (!world.isOnline)
            {
                status = world.displayName + " is offline.";
                return;
            }

            BeginOperation(ClientOperation.JoinWorld, JoinSelectedWorldRoutine());
        }

        private IEnumerator JoinSelectedWorldRoutine()
        {
            var character = characters[selectedCharacterIndex];
            var world = worlds[selectedWorldIndex];
            ShooterMmoClientSession.SelectedCharacter = character;
            ShooterMmoClientSession.SelectedWorld = world;
            ClientLog.Info(
                ClientLogCategory.Client,
                "Character '" + character.name + "' (" + character.id + ") is requesting access to world '"
                + world.id + "'.");

            JoinWorldResponse joinTicket = null;
            ShooterMmoApiError error = null;
            yield return apiClient.CreateJoinTicket(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                world.id,
                new JoinWorldRequest { characterId = character.id },
                result => joinTicket = result,
                result => error = result);

            if (HandleError(error))
            {
                yield break;
            }

            ClientLog.Info(
                ClientLogCategory.Auth,
                "AuthService issued a short-lived join ticket for character '" + character.name
                + "' and world '" + world.id + "'.");

            var worldClient = ShooterMmoClientBootstrap.WorldClient;
            if (worldClient == null)
            {
                ClientLog.Error(
                    ClientLogCategory.Client,
                    "The persistent realtime client is unavailable, so the world join cannot start.");
                HandleRealtimeError(new RealtimeClientError(
                    RealtimeClientErrorKind.Network,
                    "realtime_client_missing",
                    "The persistent realtime client is unavailable."));
                yield break;
            }

            ActivePlayerSessionResponse activeSession = null;
            RealtimeClientError realtimeError = null;
            yield return worldClient.Join(
                world.host,
                world.udpPort,
                joinTicket.joinTicket,
                result => activeSession = result,
                result => realtimeError = result);

            if (HandleRealtimeError(realtimeError))
            {
                yield break;
            }

            ShooterMmoClientSession.ActiveWorldSession = activeSession;
            status = "Joined " + activeSession.worldId + " as " + activeSession.characterName + ".";
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
