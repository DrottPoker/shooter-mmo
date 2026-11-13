using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Text;
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
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        public const string ScreenName = "CharacterSelect";

        // The codes of the join failures that the client finds itself.
        public const string WorldSceneUnavailableCode = "world_scene_unavailable";
        public const string WorldSceneLoadFailedCode = "world_scene_load_failed";

        private readonly ClientOperationState operationState = new ClientOperationState();
        private readonly CharacterSelectState state = new CharacterSelectState();

        private ApiClient apiClient;
        private WorldJoin worldJoin;
        private ClientSession clientSession;
        private ClientSessionRecovery recovery;
        private WorldExit worldExit;
        private RealtimeSession realtimeSession;
        private ClientNotices notices;
        private string exitNotice = string.Empty;
        private bool operationStepFailed;

        public CharacterSelectState State
        {
            get { return state; }
        }

        public bool IsBusy
        {
            get { return operationState.IsBusy; }
        }

        // Called by the composition root before the component is activated. The panel selects in
        // the client session, enters the World of a join, and signs out; a 401 goes to the
        // recovery, and a joined World that does not load to the World exit. The notice says why
        // the client left its last World, and the client notices tell how long a character that
        // left still stands in it.
        public void Initialize(
            ClientSession session,
            ClientSessionRecovery sessionRecovery,
            WorldExit exit,
            RealtimeSession realtime,
            TextCatalog messageTexts,
            ClientNotices clientNotices,
            string notice)
        {
            clientSession = session;
            recovery = sessionRecovery;
            worldExit = exit;
            realtimeSession = realtime;
            texts = messageTexts;
            notices = clientNotices;
            exitNotice = notice ?? string.Empty;
        }

        // Loads the characters and the shards again, keeping what is chosen.
        public void Refresh()
        {
            BeginOperation(ClientOperation.LoadSelection, LoadSelectionRoutine(null, null), texts.Get("ui/characters/refreshing"));
        }

        public void CreateCharacter()
        {
            BeginOperation(ClientOperation.CreateCharacter, CreateCharacterRoutine(), texts.Get("ui/characters/creating"));
        }

        public void LogOut()
        {
            BeginOperation(ClientOperation.Logout, LogoutRoutine(), texts.Get("ui/characters/logging-out"));
        }

        // Enters the World of the chosen shard with the chosen character.
        public void BeginJoin()
        {
            var blocker = AccountEntryPresentation.EnterWorldBlocker(state, texts);
            if (!EnsureAuthenticated() || blocker != null)
            {
                SetStatus(blocker, TemporaryUiKit.WarningColor);
                return;
            }

            BeginOperation(
                ClientOperation.JoinShard,
                JoinSelectedShardRoutine(),
                texts.Format(
                    "ui/characters/joining",
                    MessageArgument.Text("shard", state.SelectedShard.displayName),
                    MessageArgument.Text("character", state.SelectedCharacter.name)));
        }

        private void Awake()
        {
            apiClient = new ApiClient(clientSession.Settings.RequestTimeoutSeconds);
            worldJoin = new WorldJoin(clientSession, apiClient, realtimeSession);
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
                texts.Get("ui/characters/loading-selection"));
        }

        private void OnDestroy()
        {
            state.Changed -= OnStateChanged;
            apiClient?.Dispose();
            screen?.Destroy();
        }

        private void Update()
        {
            RefreshBodyNote();
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
            clientSession.Select(state.SelectedCharacter, state.SelectedShard);
            RefreshView();
        }

        private IEnumerator LogoutRoutine()
        {
            var account = clientSession.Auth;
            var accountName = account != null ? account.username : "unknown";
            var accountId = account != null ? account.accountId : "unknown";
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Logging out account '" + accountName + "' (" + accountId + ").");

            ApiError error = null;
            yield return apiClient.Logout(
                clientSession.Settings.GameServiceBaseUrl,
                clientSession.SessionToken,
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
            clientSession.SignOut();
            ClientSceneLoader.LoadLoginMenu();
        }

        // The preferred ids choose the character and shard; without them the chosen ones stay.
        private IEnumerator LoadSelectionRoutine(string preferredCharacterId, string preferredShardId)
        {
            yield return RefreshCharactersRoutine(preferredCharacterId);
            if (operationStepFailed || !clientSession.IsAuthenticated)
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

            SetStatus(AccountEntryPresentation.SelectionSummary(state, texts), TemporaryUiKit.TextSecondary);
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
                        SetStatus(texts.Get("ui/characters/no-body-offered"), TemporaryUiKit.DangerColor);
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
                clientSession.Settings.GameServiceBaseUrl,
                clientSession.SessionToken,
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
            SetStatus(
                texts.Format("ui/characters/created", MessageArgument.Text("character", createdCharacter.name)),
                TemporaryUiKit.SuccessColor);
        }

        // What character creation offers; without it no character can be created.
        private IEnumerator LoadCreationLookRoutine()
        {
            CharacterAppearanceCatalogResponse result = null;
            ApiError error = null;
            yield return apiClient.GetCharacterAppearanceCatalog(
                clientSession.Settings.GameServiceBaseUrl,
                clientSession.SessionToken,
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
                clientSession.Settings.GameServiceBaseUrl,
                clientSession.SessionToken,
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
                clientSession.Settings.GameServiceBaseUrl,
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
                SetJoinError(JoinFailed(shard, WorldSceneUnavailable(worldSceneError)));
                yield break;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                SetJoinError(SceneMissing(shard, worldSceneName));
                yield break;
            }

            clientSession.Select(character, shard);

            // The ticket and the join are the part that the reconnect shares; the ticket's World
            // must be one this client has, and the ticket's shard is the placement it joins.
            WorldJoinResult join = null;
            yield return worldJoin.Run(
                character.id,
                character.name,
                shard.id,
                ticket => AcceptTicket(shard, ticket, out worldSceneName),
                result => join = result);

            switch (join.Status)
            {
                case WorldJoinStatus.TicketRefused:
                    HandleError(join.TicketError);
                    yield break;
                case WorldJoinStatus.TicketIncomplete:
                    SetJoinError(texts.Format(
                        "ui/characters/join-incomplete",
                        MessageArgument.Text("shard", shard.displayName)));
                    yield break;
                case WorldJoinStatus.TicketDeclined:
                    yield break;
                case WorldJoinStatus.JoinFailed:
                    HandleRealtimeError(join.JoinError);
                    yield break;
            }

            var activeSession = join.Session;
            clientSession.EnterWorld(activeSession);
            AccountEntryPreferences.RememberWorldEntry(character.id, shard.id);
            SetStatus(
                texts.Format(
                    "ui/characters/joined",
                    MessageArgument.Text("shard", activeSession.shardId),
                    MessageArgument.Text("character", activeSession.characterName),
                    MessageArgument.Text("scene", worldSceneName)),
                TemporaryUiKit.SuccessColor);

            // A successful load replaces this scene and ends the routine. A failed load keeps
            // CharacterSelect, so the World exit releases the joined session.
            var sceneLoad = ClientSceneLoader.LoadWorldScene(worldSceneName);
            yield return sceneLoad;
            if (sceneLoad.Status == AsyncOperationStatus.Succeeded)
            {
                yield break;
            }

            SetJoinError(texts.Format(
                "ui/characters/enter-failed",
                MessageArgument.Text("scene", worldSceneName),
                MessageArgument.Text(
                    "reason",
                    ClientMessages.Text(
                        texts,
                        WorldSceneLoadFailedCode,
                        sceneLoad.OperationException?.Message))));
            yield return worldExit.Release(activeSession);
        }

        // Resolves the scene of the ticket's World again, so a shard list cached across an
        // offline rebind cannot load the previous scene, and selects the ticket's shard.
        private bool AcceptTicket(ShardResponse shard, JoinShardResponse ticket, out string worldSceneName)
        {
            if (!WorldSceneCatalog.TryResolveScene(
                    ticket.shard.worldId,
                    out worldSceneName,
                    out var worldSceneError))
            {
                SetJoinError(JoinFailed(shard, WorldSceneUnavailable(worldSceneError)));
                return false;
            }

            if (!ClientSceneLoader.IsWorldSceneAvailable(worldSceneName))
            {
                SetJoinError(SceneMissing(shard, worldSceneName));
                return false;
            }

            clientSession.Select(clientSession.SelectedCharacter, ticket.shard);
            return true;
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
            if (clientSession.IsAuthenticated)
            {
                return true;
            }

            clientSession.SignOut();
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

            if (!recovery.ReturnToLoginIfUnauthorized(error))
            {
                SetStatus(
                    AccountEntryPresentation.ErrorText(error, clientSession.Settings.GameServiceBaseUrl, texts),
                    TemporaryUiKit.DangerColor);
                ClientLog.Error(ClientLogCategory.Client, "API request failed: " + error.ToDiagnostic());
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
            SetStatus(AccountEntryPresentation.ErrorText(error, texts), TemporaryUiKit.DangerColor);
            return true;
        }

        // A World that the scene catalog of this client cannot resolve. The catalog's
        // explanation goes to the log.
        private string WorldSceneUnavailable(string diagnostic)
        {
            return ClientMessages.Text(texts, WorldSceneUnavailableCode, diagnostic);
        }

        // A join failure whose reason another class explains.
        private string JoinFailed(ShardResponse shard, string reason)
        {
            return texts.Format(
                "ui/characters/join-failed",
                MessageArgument.Text("shard", shard.displayName),
                MessageArgument.Text("reason", reason));
        }

        private string SceneMissing(ShardResponse shard, string sceneName)
        {
            return texts.Format(
                "ui/characters/join-scene-missing",
                MessageArgument.Text("shard", shard.displayName),
                MessageArgument.Text("scene", sceneName));
        }

        private void SetJoinError(string message)
        {
            operationStepFailed = true;
            SetStatus(message, TemporaryUiKit.DangerColor);
            ClientLog.Error(ClientLogCategory.Client, message);
        }
    }
}
