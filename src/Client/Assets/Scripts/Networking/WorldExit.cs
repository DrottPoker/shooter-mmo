using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Networking
{
    // Owns every way out of a World but a forced sign-out: the graceful leave, the release of a
    // joined World that a scene change abandoned or that never loaded, a World scene that starts
    // without a joined connection, a connection that the worker or the client ended, and a lost
    // connection that the reconnect could not restore. Each leaves the client session's World by
    // the exact simulation session id, so an exit that completes late never clears a newer World,
    // and an exit that ends the World scene tells the next entry screen why. A connection that the
    // worker closed because the account logged in elsewhere signs out through
    // ClientSessionRecovery. The bootstrap creates one.
    public sealed class WorldExit
    {
        // The notice of a lost connection that the reconnect gave up on.
        public const string ReconnectFailedCode = "reconnect_failed";

        private readonly ClientSession clientSession;
        private readonly RealtimeSession realtimeSession;
        private readonly ClientSessionRecovery recovery;
        private readonly ClientNotices notices;

        public WorldExit(
            ClientSession session,
            RealtimeSession realtime,
            ClientSessionRecovery sessionRecovery,
            ClientNotices clientNotices)
        {
            clientSession = session ?? throw new ArgumentNullException(nameof(session));
            realtimeSession = realtime ?? throw new ArgumentNullException(nameof(realtime));
            recovery = sessionRecovery ?? throw new ArgumentNullException(nameof(sessionRecovery));
            notices = clientNotices ?? throw new ArgumentNullException(nameof(clientNotices));
        }

        // Leaves the World gracefully and loads CharacterSelect, which shows how long the body
        // stays. A refused leave that keeps the connection joined stays in the World; any other
        // failure leaves it all the same. The error goes to onError before onLeft. While a lost
        // connection is restored, the connection is not joined to this World's session, and a leave
        // would name that session on another connection: the World is left at once, and a join
        // that the reconnect runs is closed.
        public IEnumerator Leave(Action<RealtimeClientError> onError, Action onLeft)
        {
            var world = clientSession.ActiveSimulationSession;
            if (world == null)
            {
                ClientSceneLoader.LoadCharacterSelect();
                yield break;
            }

            if (!IsJoinedTo(world))
            {
                if (realtimeSession.State != RealtimeConnectionState.Disconnected)
                {
                    realtimeSession.Abort();
                }

                clientSession.LeaveWorld(world.simulationSessionId);
                onLeft?.Invoke();
                ClientSceneLoader.LoadCharacterSelect();
                yield break;
            }

            RealtimeClientError error = null;
            var bodySeconds = 0;
            yield return realtimeSession.Leave(
                world.simulationSessionId,
                value => bodySeconds = value,
                value => error = value);

            if (error != null)
            {
                onError?.Invoke(error);
                if (realtimeSession.IsJoined)
                {
                    yield break;
                }
            }
            else
            {
                notices.RememberBody(world.characterId, bodySeconds);
            }

            clientSession.LeaveWorld(world.simulationSessionId);
            onLeft?.Invoke();
            ClientSceneLoader.LoadCharacterSelect();
        }

        // Releases a World that the scene flow left, or never entered, without the graceful
        // leave: the leave runs while the connection is joined to that session, a failed one
        // closes UDP so the worker can run its disconnect cleanup, and then the World of that
        // session is left. A connection joined to another session stays.
        public IEnumerator Release(ActiveSimulationSessionResponse world)
        {
            if (world == null)
            {
                yield break;
            }

            RealtimeClientError error = null;
            var left = false;
            var bodySeconds = 0;
            if (IsJoinedTo(world))
            {
                yield return realtimeSession.Leave(
                    world.simulationSessionId,
                    value =>
                    {
                        left = true;
                        bodySeconds = value;
                    },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDiagnostic());
                realtimeSession.Abort();
            }

            if (left)
            {
                notices.RememberBody(world.characterId, bodySeconds);
            }

            clientSession.LeaveWorld(world.simulationSessionId);
        }

        // A World scene that starts while its connection is no longer joined returns to
        // CharacterSelect.
        public void LeaveUnjoinedWorld()
        {
            var world = clientSession.ActiveSimulationSession;
            if (world != null)
            {
                clientSession.LeaveWorld(world.simulationSessionId);
            }

            ClientSceneLoader.LoadCharacterSelect();
        }

        // The worker or the client ended the joined session in a way that does not reconnect: the
        // client leaves its World and the World scene, to CharacterSelect, or to LoginMenu without
        // an account session, and the screen shows the text of the code. A new login of the
        // account signs out through the recovery instead.
        public void LeaveEndedWorld(RealtimeClientError error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }

            if (string.Equals(
                error.Code,
                ClientSessionRecovery.AccountSessionReplacedCode,
                StringComparison.Ordinal))
            {
                recovery.ReturnToLoginAfterReplacement(error);
                return;
            }

            ClientLog.Warning(
                ClientLogCategory.Client,
                "The active simulation connection closed. Clearing local shard state and leaving the world scene: "
                + error.ToDiagnostic());
            var world = clientSession.ActiveSimulationSession;
            LeaveWorldScene(world != null ? world.simulationSessionId : null, error.Code);
        }

        // The reconnect gave up on the lost connection of this simulation session: the World of
        // that session is left, and CharacterSelect says that the connection could not be restored.
        public void LeaveLostWorld(string simulationSessionId)
        {
            LeaveWorldScene(simulationSessionId, ReconnectFailedCode);
        }

        private void LeaveWorldScene(string simulationSessionId, string noticeCode)
        {
            if (simulationSessionId != null)
            {
                clientSession.LeaveWorld(simulationSessionId);
            }

            notices.Post(noticeCode);
            if (!WorldSceneCatalog.IsWorldScene(SceneManager.GetActiveScene().name))
            {
                return;
            }

            if (clientSession.IsAuthenticated)
            {
                ClientSceneLoader.LoadCharacterSelect();
            }
            else
            {
                ClientSceneLoader.LoadLoginMenu();
            }
        }

        private bool IsJoinedTo(ActiveSimulationSessionResponse world)
        {
            return realtimeSession.IsJoined
                && string.Equals(
                    realtimeSession.JoinedSession?.simulationSessionId,
                    world.simulationSessionId,
                    StringComparison.Ordinal);
        }
    }
}
