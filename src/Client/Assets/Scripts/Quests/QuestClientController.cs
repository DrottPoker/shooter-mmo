using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Quests
{
    // Keeps the joined character's quest log: it reads GameService's log when the character
    // joins and applies the realtime quest changes in revision order. Changes that arrive while
    // a read is in flight wait and follow it. A change that does not follow the log, or a reload
    // notice after an uncertain answer, reads the log again.
    [DisallowMultipleComponent]
    public sealed class QuestClientController : MonoBehaviour
    {
        private const int MaximumLoadAttempts = 3;
        private const float LoadRetryDelaySeconds = 0.5f;

        private readonly List<RealtimeQuestLogChanged> waitingChanges = new List<RealtimeQuestLogChanged>();
        private ApiClient apiClient;
        private RealtimeSimulationClient simulationClient;
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

        public void Initialize(ApiClient client, RealtimeSimulationClient realtimeClient, TextAsset questCatalog)
        {
            if (initialized)
            {
                return;
            }

            apiClient = client ?? throw new ArgumentNullException(nameof(client));
            simulationClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            if (ClientQuestCatalog.TryLoad(questCatalog, out var catalog, out var error))
            {
                State.SetCatalog(catalog);
            }
            else
            {
                ClientLog.Warning(ClientLogCategory.Client, "The quest windows are unavailable: " + error);
                State.SetFailed("quest_catalog_invalid", error);
            }

            simulationClient.QuestLogChanged += OnQuestLogChanged;
            simulationClient.SessionEnded += OnSessionEnded;
            initialized = true;
            observedJoin = simulationClient.IsJoined;
            ObserveSessionIdentity();
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

            observedJoin = joined;
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.QuestLogChanged -= OnQuestLogChanged;
                simulationClient.SessionEnded -= OnSessionEnded;
            }
        }

        private void OnQuestLogChanged(RealtimeQuestLogChanged change)
        {
            if (loading || State.Status == QuestClientStatus.Loading)
            {
                waitingChanges.Add(change);
                return;
            }

            if (State.ApplyChange(change) == QuestLogApplyResult.ReloadRequired)
            {
                Reload();
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            loadGeneration++;
            loading = false;
            reloadQueued = false;
            waitingChanges.Clear();
            observedJoin = false;
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
                State.SetFailed(lastError.Code, lastError.Message);
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
