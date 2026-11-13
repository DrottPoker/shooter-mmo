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
    // without a joined connection, and a connection that the worker or the network closed. Each
    // leaves the client session's World by the exact simulation session id, so an exit that
    // completes late never clears a newer World. A connection that the worker closed because the
    // account logged in elsewhere signs out through ClientSessionRecovery. The bootstrap creates
    // one and disposes it.
    public sealed class WorldExit : IDisposable
    {
        private readonly ClientSession clientSession;
        private readonly RealtimeSession realtimeSession;
        private readonly ClientSessionRecovery recovery;

        public WorldExit(ClientSession session, RealtimeSession realtime, ClientSessionRecovery sessionRecovery)
        {
            clientSession = session ?? throw new ArgumentNullException(nameof(session));
            realtimeSession = realtime ?? throw new ArgumentNullException(nameof(realtime));
            recovery = sessionRecovery ?? throw new ArgumentNullException(nameof(sessionRecovery));
            realtimeSession.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
        }

        public void Dispose()
        {
            realtimeSession.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
        }

        // Leaves the World gracefully and loads CharacterSelect. A refused leave that keeps the
        // connection joined stays in the World; any other failure leaves it all the same. The
        // error goes to onError before onLeft.
        public IEnumerator Leave(Action<RealtimeClientError> onError, Action onLeft)
        {
            var world = clientSession.ActiveSimulationSession;
            if (world == null)
            {
                ClientSceneLoader.LoadCharacterSelect();
                yield break;
            }

            RealtimeClientError error = null;
            yield return realtimeSession.Leave(
                world.simulationSessionId,
                () => { },
                value => error = value);

            if (error != null)
            {
                onError?.Invoke(error);
                if (realtimeSession.IsJoined)
                {
                    yield break;
                }
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
            if (realtimeSession.IsJoined
                && string.Equals(
                    realtimeSession.JoinedSession?.simulationSessionId,
                    world.simulationSessionId,
                    StringComparison.Ordinal))
            {
                yield return realtimeSession.Leave(
                    world.simulationSessionId,
                    () => { },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDiagnostic());
                realtimeSession.Abort();
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

        // The worker or the network ended the joined session: the client leaves its World and the
        // World scene, to CharacterSelect, or to LoginMenu without an account session.
        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
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
            if (world != null)
            {
                clientSession.LeaveWorld(world.simulationSessionId);
            }

            if (WorldSceneCatalog.IsWorldScene(SceneManager.GetActiveScene().name))
            {
                if (clientSession.IsAuthenticated)
                {
                    ClientSceneLoader.LoadCharacterSelect();
                }
                else
                {
                    ClientSceneLoader.LoadLoginMenu();
                }
            }
        }
    }
}
