using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Ui
{
    // The temporary CharacterSelect screen: the account's characters on the left, and on the right
    // either the chosen character with the shards and Enter World, or the creation of a new
    // character. It loads the characters, the shards, and what creation offers one after the
    // other, chooses the character and shard of this computer's last Enter World, and runs one
    // operation at a time. Enter and a double click on a character enter the World. The widgets
    // are in CharacterSelectPanel.Layout.cs.
    public sealed partial class CharacterSelectPanel : MonoBehaviour
    {
        public const string ScreenName = "CharacterSelect";

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly CharacterSelectState state = new CharacterSelectState();

        private ApiClient apiClient;
        private RealtimeSimulationClient simulationClient;
        private bool operationStepFailed;

        public CharacterSelectState State
        {
            get { return state; }
        }

        public bool IsBusy
        {
            get { return operationState.IsBusy; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(RealtimeSimulationClient realtimeClient)
        {
            simulationClient = realtimeClient;
        }

        // Loads the characters and the shards again, keeping what is chosen.
        public void Refresh()
        {
            BeginOperation(ClientOperation.LoadSelection, LoadSelectionRoutine(null, null), "Refreshing characters and shards...");
        }

        public void CreateCharacter()
        {
            BeginOperation(ClientOperation.CreateCharacter, CreateCharacterRoutine(), "Creating the character...");
        }

        public void LogOut()
        {
            BeginOperation(ClientOperation.Logout, LogoutRoutine(), "Logging out...");
        }

        // Enters the World of the chosen shard with the chosen character.
        public void BeginJoin()
        {
            var blocker = AccountEntryPresentation.EnterWorldBlocker(state);
            if (!EnsureAuthenticated() || blocker != null)
            {
                SetStatus(blocker, TemporaryUiKit.WarningColor);
                return;
            }

            BeginOperation(
                ClientOperation.JoinShard,
                JoinSelectedShardRoutine(),
                "Joining " + state.SelectedShard.displayName + " as " + state.SelectedCharacter.name + "...");
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

            BuildUi();
            state.Changed += OnStateChanged;
            OnStateChanged();
            BeginOperation(
                ClientOperation.LoadSelection,
                LoadSelectionRoutine(AccountEntryPreferences.LastCharacterId, AccountEntryPreferences.LastShardId),
                "Loading characters and shards...");
        }

        private void OnDestroy()
        {
            state.Changed -= OnStateChanged;
            apiClient?.Dispose();
            screen?.Destroy();
        }

        private void Update()
        {
            if (screen == null || !screen.ReadKeys() || operationState.IsBusy)
            {
                return;
            }

            if (state.IsCreating)
            {
                CreateCharacter();
            }
            else
            {
                BeginJoin();
            }
        }

        private void OnStateChanged()
        {
            ClientSession.SelectedCharacter = state.SelectedCharacter;
            ClientSession.SelectedShard = state.SelectedShard;
            RefreshView();
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

        // The preferred ids choose the character and shard; without them the chosen ones stay.
        private IEnumerator LoadSelectionRoutine(string preferredCharacterId, string preferredShardId)
        {
            yield return RefreshCharactersRoutine(preferredCharacterId);
            if (operationStepFailed || !ClientSession.IsAuthenticated)
            {
                yield break;
            }

            yield return RefreshShardsRoutine(preferredShardId);
            if (operationStepFailed)
            {
                yield break;
            }

            if (state.CreationLook == null || !state.CreationLook.IsAvailable)
            {
                yield return LoadCreationLookRoutine();
                if (operationStepFailed)
                {
                    yield break;
                }
            }

            SetStatus(AccountEntryPresentation.SelectionSummary(state), TemporaryUiKit.TextSecondary);
        }

        private IEnumerator CreateCharacterRoutine()
        {
            if (!EnsureAuthenticated())
            {
                yield break;
            }

            // JsonUtility writes a missing appearance as an empty one, which GameService refuses, so
            // a character is always created with the offers loaded.
            if (state.CreationLook == null || !state.CreationLook.IsAvailable)
            {
                yield return LoadCreationLookRoutine();
                if (state.CreationLook == null || !state.CreationLook.IsAvailable)
                {
                    if (!operationStepFailed)
                    {
                        SetStatus("GameService offers no body to create a character with.", TemporaryUiKit.DangerColor);
                    }

                    yield break;
                }
            }

            CharacterResponse createdCharacter = null;
            ApiError error = null;
            var request = new CreateCharacterRequest
            {
                name = CharacterNameInput,
                appearance = state.CreationLook.ToRequest()
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

            ClientLog.Info(
                ClientLogCategory.Client,
                "Created character '" + createdCharacter.name + "' (" + createdCharacter.id + ").");
            yield return RefreshCharactersRoutine(createdCharacter.id);
            if (operationStepFailed)
            {
                yield break;
            }

            ClearCharacterNameInput();
            state.SetCreating(false);
            SetStatus("Created " + createdCharacter.name + ". Choose a shard and enter the World.", TemporaryUiKit.SuccessColor);
        }

        // What character creation offers; without it no character can be created.
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

            state.SetCreationLook(new CharacterCreationLook(result));
        }

        private IEnumerator RefreshCharactersRoutine(string preferredId)
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

            state.SetCharacters(result, preferredId);
        }

        private IEnumerator RefreshShardsRoutine(string preferredId)
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

            state.SetShards(result, preferredId);
        }

        private IEnumerator JoinSelectedShardRoutine()
        {
            var character = state.SelectedCharacter;
            var shard = state.SelectedShard;
            if (!WorldSceneCatalog.TryResolveScene(
                    shard.worldId,
                    out var worldSceneName,
                    out var worldSceneError))
            {
                SetJoinError("Cannot join " + shard.displayName + ": " + worldSceneError);
                yield break;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                SetJoinError("Cannot join " + shard.displayName + ": scene '" + worldSceneName
                    + "' is not an Addressables World scene in this client build.");
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
                SetJoinError("Cannot join " + shard.displayName
                    + ": GameService returned incomplete placement data.");
                yield break;
            }

            if (!WorldSceneCatalog.TryResolveScene(
                    joinTicket.shard.worldId,
                    out worldSceneName,
                    out worldSceneError))
            {
                SetJoinError("Cannot join " + shard.displayName + ": " + worldSceneError);
                yield break;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                SetJoinError("Cannot join " + shard.displayName + ": scene '" + worldSceneName
                    + "' is not an Addressables World scene in this client build.");
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
            AccountEntryPreferences.RememberWorldEntry(character.id, shard.id);
            SetStatus(
                "Joined shard " + activeSession.shardId + " as " + activeSession.characterName
                + ". Loading " + worldSceneName + ".",
                TemporaryUiKit.SuccessColor);

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
            SetJoinError("Cannot enter " + worldSceneName + ": the scene failed to load (" + loadError + ").");
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

        private void BeginOperation(ClientOperation operation, IEnumerator routine, string progress)
        {
            if (!operationState.TryBegin(operation))
            {
                return;
            }

            operationStepFailed = false;
            SetStatus(progress, TemporaryUiKit.TextSecondary);
            RefreshBusy();
            StartCoroutine(RunOperation(operation, routine));
        }

        private IEnumerator RunOperation(ClientOperation operation, IEnumerator routine)
        {
            yield return routine;
            operationState.Complete(operation);
            RefreshBusy();
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
                SetStatus(AccountEntryPresentation.ErrorText(error, ClientSession.GameServiceBaseUrl), TemporaryUiKit.DangerColor);
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
            SetStatus(AccountEntryPresentation.ErrorText(error), TemporaryUiKit.DangerColor);
            return true;
        }

        private void SetJoinError(string message)
        {
            operationStepFailed = true;
            SetStatus(message, TemporaryUiKit.DangerColor);
            ClientLog.Error(ClientLogCategory.Client, message);
        }
    }
}
