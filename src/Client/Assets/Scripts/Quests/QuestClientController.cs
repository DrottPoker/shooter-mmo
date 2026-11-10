using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;
using UnityEngine;

namespace Mmorpg.Client.Quests
{
    // Keeps the joined character's quest log: it reads GameService's log when the character
    // joins and applies the realtime quest changes in revision order. Changes that arrive while
    // a read is in flight wait and follow it. A change that does not follow the log, or a reload
    // notice after an uncertain answer, reads the log again. A read that failed is tried again,
    // as QuestLogRetry decides.
    [DisallowMultipleComponent]
    public sealed class QuestClientController : MonoBehaviour
    {
        private const int MaximumLoadAttempts = 3;
        private const float LoadRetryDelaySeconds = 0.5f;

        private readonly List<RealtimeQuestLogChanged> waitingChanges = new List<RealtimeQuestLogChanged>();
        private readonly QuestLogRetry retry = new QuestLogRetry();
        private ApiClient apiClient;
        private TextCatalog messageTexts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private RealtimeSimulationClient simulationClient;
        private IInventoryOperationQueue inventory;
        private Guid pendingIntentId;
        private bool initialized;
        private bool observedJoin;
        private bool loading;
        private bool reloadQueued;
        // Advanced when the character changes or the session ends, so a read of the previous one
        // ends at its answer.
        private int loadGeneration;
        private string observedAccountId = string.Empty;
        private string observedCharacterId = string.Empty;

        public QuestClientState State { get; } = new QuestClientState();

        // Raised when an abandon is sent or answered, so the quest log shows its state.
        public event Action IntentChanged;

        // A quest item that a dying Mob left in its corpse for one of the character's quests.
        public event Action<RealtimeQuestDropInCorpse> QuestDropped;

        public bool HasPendingIntent
        {
            get { return pendingIntentId != Guid.Empty; }
        }

        // The kind of the last quest intent that was sent, and why it was refused, or empty. Both
        // belong to the session that sent it.
        public RealtimeQuestIntentKind LastIntentKind { get; private set; }

        public string LastIntentError { get; private set; } = string.Empty;

        // Why the last abandon was refused, or empty: the quest log shows it, while the Admin
        // menu reports the refusals of its tools itself.
        public string LastAbandonError
        {
            get { return LastIntentKind == RealtimeQuestIntentKind.Abandon ? LastIntentError : string.Empty; }
        }

        public void Initialize(
            ApiClient client,
            RealtimeSimulationClient realtimeClient,
            TextAsset questCatalog,
            TextCatalog texts,
            IInventoryOperationQueue inventoryQueue = null)
        {
            if (initialized)
            {
                return;
            }

            apiClient = client ?? throw new ArgumentNullException(nameof(client));
            simulationClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            inventory = inventoryQueue;
            messageTexts = texts ?? throw new ArgumentNullException(nameof(texts));
            if (ClientQuestCatalog.TryLoad(questCatalog, texts, out var catalog, out var error))
            {
                State.SetCatalog(catalog);
            }
            else
            {
                ClientLog.Warning(ClientLogCategory.Client, "The quest windows are unavailable: " + error);
                State.SetFailed("quest_catalog_invalid", error);
            }

            simulationClient.QuestLogChanged += OnQuestLogChanged;
            simulationClient.QuestIntentCompleted += OnQuestIntentCompleted;
            simulationClient.QuestDropInCorpse += OnQuestDropInCorpse;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;
            observedJoin = simulationClient.IsJoined;
            ObserveSessionIdentity();
        }

        // Abandons an active quest from anywhere. The worker queues it behind the character's
        // other work; the quest change and the removed quest items arrive before its answer.
        public bool TryAbandon(string questId, out string error)
        {
            error = string.Empty;
            if (!initialized || !State.IsReady || !State.IsActive(questId))
            {
                error = "That quest is not active.";
                return false;
            }

            return TrySend(RealtimeQuestIntent.CreateAbandon(Guid.NewGuid(), questId), out error);
        }

        // Runs a development quest tool of the Admin menu, such as accepting any quest or making
        // one ready; only a Development worker carries it out. Its answer arrives like an abandon's.
        public bool TryRunAdminTool(RealtimeQuestIntent intent, out string error)
        {
            error = string.Empty;
            if (intent == null || intent.Kind == RealtimeQuestIntentKind.Abandon)
            {
                throw new ArgumentException("An admin tool is a development quest intent.", nameof(intent));
            }

            if (!initialized || !State.IsReady)
            {
                error = "Your quests are not loaded yet.";
                return false;
            }

            return TrySend(intent, out error);
        }

        private bool TrySend(RealtimeQuestIntent intent, out string error)
        {
            error = string.Empty;
            if (pendingIntentId != Guid.Empty)
            {
                error = "Wait for the last quest action to finish.";
                return false;
            }

            if (!simulationClient.TrySendQuestIntent(intent))
            {
                error = "The quest request could not be sent.";
                return false;
            }

            pendingIntentId = intent.OperationId;
            LastIntentKind = intent.Kind;
            LastIntentError = string.Empty;
            IntentChanged?.Invoke();
            return true;
        }

        // Reads the quest log at once when a read failed, such as for a quest window that needs it.
        public void EnsureLoaded()
        {
            if (State.Status == QuestClientStatus.Failed)
            {
                Reload();
            }
        }

        // Reads GameService's quest log again, after the read in flight when there is one.
        public void Reload()
        {
            if (!initialized || State.Catalog == null || State.Status == QuestClientStatus.UpdateRequired)
            {
                return;
            }

            if (loading)
            {
                reloadQueued = true;
                return;
            }

            if (!TryGetSelectedCharacterId(out _))
            {
                return;
            }

            loading = true;
            StartCoroutine(LoadRoutine(loadGeneration));
        }

        private void Update()
        {
            if (!initialized)
            {
                return;
            }

            var identityChanged = ObserveSessionIdentity();
            var joined = simulationClient.IsJoined;
            if (joined && (!observedJoin || identityChanged))
            {
                Reload();
            }
            else if (joined
                && !loading
                && State.Status == QuestClientStatus.Failed
                && retry.IsDue(Time.unscaledTime))
            {
                Reload();
            }

            observedJoin = joined;
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.QuestLogChanged -= OnQuestLogChanged;
                simulationClient.QuestIntentCompleted -= OnQuestIntentCompleted;
                simulationClient.QuestDropInCorpse -= OnQuestDropInCorpse;
                simulationClient.SessionEnded -= OnSessionEnded;
            }
        }

        private void OnQuestLogChanged(RealtimeQuestLogChanged change)
        {
            // A change that committed behind lost answers may have moved items too, such as the
            // rewards of a turn-in, so the inventory is read again with the log.
            if (change != null && change.ReloadRequired)
            {
                inventory?.RefreshFullState();
            }

            if (loading || State.Status == QuestClientStatus.Loading)
            {
                waitingChanges.Add(change);
                return;
            }

            // The log moved on while it could not be read, so it is read again now.
            if (State.Status == QuestClientStatus.Failed)
            {
                Reload();
                return;
            }

            if (State.ApplyChange(change) == QuestLogApplyResult.ReloadRequired)
            {
                Reload();
            }
        }

        private void OnQuestIntentCompleted(RealtimeQuestIntentResult result)
        {
            if (result == null || result.OperationId != pendingIntentId)
            {
                return;
            }

            pendingIntentId = Guid.Empty;
            LastIntentError = result.Succeeded
                ? string.Empty
                : ClientMessages.Text(
                    messageTexts,
                    result.Error?.Code ?? "quest_intent_failed",
                    result.Error?.Message);
            if (result.Succeeded && result.ItemStateRevision > 0)
            {
                inventory?.EnsureFullState(result.ItemStateRevision);
            }

            IntentChanged?.Invoke();
        }

        private void OnQuestDropInCorpse(RealtimeQuestDropInCorpse drop)
        {
            QuestDropped?.Invoke(drop);
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            loadGeneration++;
            loading = false;
            reloadQueued = false;
            waitingChanges.Clear();
            observedJoin = false;
            ForgetIntent();
        }

        // A pending intent and the refusal of the last one end with their session.
        private void ForgetIntent()
        {
            if (pendingIntentId == Guid.Empty && string.IsNullOrEmpty(LastIntentError))
            {
                return;
            }

            pendingIntentId = Guid.Empty;
            LastIntentError = string.Empty;
            IntentChanged?.Invoke();
        }

        private IEnumerator LoadRoutine(int generation)
        {
            State.BeginLoading();
            ApiError lastError = null;
            for (var attempt = 0; attempt < MaximumLoadAttempts; attempt++)
            {
                if (!TryGetSelectedCharacterId(out var characterId))
                {
                    break;
                }

                CharacterQuestLogResponse response = null;
                ApiError apiError = null;
                yield return apiClient.GetCharacterQuests(
                    ClientSession.GameServiceBaseUrl,
                    ClientSession.SessionToken,
                    characterId.ToString(),
                    value => response = value,
                    value => apiError = value);
                if (generation != loadGeneration)
                {
                    yield break;
                }

                if (apiError != null)
                {
                    if (ClientSessionRecovery.ReturnToLoginIfUnauthorized(apiError, simulationClient))
                    {
                        loading = false;
                        yield break;
                    }

                    lastError = apiError;
                }
                else if (State.TryApplyLog(response, out var applyError))
                {
                    lastError = null;
                    retry.Reset();
                    ApplyWaitingChanges();
                    break;
                }
                else
                {
                    if (State.Status == QuestClientStatus.UpdateRequired)
                    {
                        lastError = null;
                        break;
                    }

                    lastError = new ApiError(ApiErrorKind.InvalidResponse, 0, "quest_log_invalid", applyError, string.Empty);
                }

                if (attempt + 1 < MaximumLoadAttempts)
                {
                    yield return new WaitForSecondsRealtime(LoadRetryDelaySeconds);
                    if (generation != loadGeneration)
                    {
                        yield break;
                    }
                }
            }

            if (lastError != null)
            {
                State.SetFailed(lastError.Code, ClientMessages.Text(messageTexts, lastError));
                retry.Failed(Time.unscaledTime);
            }

            loading = false;
            if (reloadQueued)
            {
                reloadQueued = false;
                Reload();
            }
        }

        // The changes that arrived during the read follow it in revision order; one that the read
        // already holds is stale, and one after a gap reads the log again.
        private void ApplyWaitingChanges()
        {
            var changes = waitingChanges.OrderBy(change => change.QuestLogRevision).ToArray();
            waitingChanges.Clear();
            foreach (var change in changes)
            {
                if (State.ApplyChange(change) == QuestLogApplyResult.ReloadRequired)
                {
                    reloadQueued = true;
                    return;
                }
            }
        }

        private bool ObserveSessionIdentity()
        {
            var accountId = ClientSession.Auth?.accountId ?? string.Empty;
            var character = ClientSession.SelectedCharacter;
            var characterId = character?.id ?? string.Empty;
            if (string.Equals(accountId, observedAccountId, StringComparison.Ordinal)
                && string.Equals(characterId, observedCharacterId, StringComparison.Ordinal))
            {
                return false;
            }

            observedAccountId = accountId;
            observedCharacterId = characterId;
            loadGeneration++;
            loading = false;
            reloadQueued = false;
            waitingChanges.Clear();
            retry.Reset();
            ForgetIntent();
            State.PrepareCharacter(
                Guid.TryParse(characterId, out var parsed) ? parsed : Guid.Empty,
                character?.level ?? 1);
            return true;
        }

        private static bool TryGetSelectedCharacterId(out Guid characterId)
        {
            return Guid.TryParse(ClientSession.SelectedCharacter?.id, out characterId)
                && characterId != Guid.Empty;
        }
    }
}
