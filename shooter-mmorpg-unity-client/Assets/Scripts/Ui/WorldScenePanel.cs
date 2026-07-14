using System.Collections;
using ShooterMmo.Gameplay;
using ShooterMmo.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class WorldScenePanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private string status = "Realtime simulation connection active.";
        private Vector2 scrollPosition;
        private LocalPlayerInput localPlayerInput;
        private bool isVisible = true;

        private void Start()
        {
            localPlayerInput = FindAnyObjectByType<LocalPlayerInput>();
            if (ShooterMmoClientSession.ActiveSimulationSession == null)
            {
#if UNITY_EDITOR
                status = "No active simulation session. Local player is not spawned.";
                return;
#else
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                return;
#endif
            }

            if (ShooterMmoClientBootstrap.SimulationClient == null
                || !ShooterMmoClientBootstrap.SimulationClient.IsJoined)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
            }
        }

        private void Update()
        {
            if (localPlayerInput == null)
            {
                localPlayerInput = FindAnyObjectByType<LocalPlayerInput>();
            }

            if (localPlayerInput != null && localPlayerInput.ToggleWorldDebugPressedThisFrame)
            {
                isVisible = !isVisible;
            }
        }

        private void OnGUI()
        {
            if (!isVisible)
            {
                return;
            }

            GUILayout.BeginArea(
                TemporaryPanelStyles.GetBottomLeftPanelRect(420f, 300f),
                "World Debug (F2)",
                GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            var previousGuiState = GUI.enabled;
            GUI.enabled = previousGuiState && !operationState.IsBusy;
            DrawSessionSection();
            DrawTransportSection();
            GUI.enabled = previousGuiState;

            GUILayout.Space(6f);
            TemporaryPanelStyles.DrawStatus(operationState.IsBusy, status, 44f);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawSessionSection()
        {
            GUILayout.Label("Local Session");

            var session = ShooterMmoClientSession.ActiveSimulationSession;
            if (session != null)
            {
                GUILayout.Label("Character: " + session.characterName);
                GUILayout.Label("Shard: " + session.shardId);
                GUILayout.Label("World: " + session.worldId);
                GUILayout.Label("Worker: " + session.workerId);
                GUILayout.Label("Runtime: " + session.workerRuntimeId);
                GUILayout.Label("Session: " + session.simulationSessionId);
                GUILayout.Label("Reconnect: " + session.isReconnect);
            }
            else
            {
                GUILayout.Label("No local simulation session.");
            }

            if (GUILayout.Button("Leave Shard", GUILayout.Height(28f)))
            {
                BeginLeave();
            }

            GUILayout.Space(6f);
        }

        private void DrawTransportSection()
        {
            GUILayout.Label("Realtime Transport");
            var simulationClient = ShooterMmoClientBootstrap.SimulationClient;
            if (simulationClient == null)
            {
                GUILayout.Label("Client unavailable");
                return;
            }

            GUILayout.Label("State: " + simulationClient.State);
            GUILayout.Label("Endpoint: " + simulationClient.ConnectedHost + ":" + simulationClient.ConnectedPort + "/udp");
            if (simulationClient.MovementSession != null)
            {
                GUILayout.Label("Authority: SimulationWorker");
                GUILayout.Label("Server Tick: " + simulationClient.LatestServerTick);
                GUILayout.Label(
                    "Simulation: " + simulationClient.MovementSession.Settings.TickRateHz
                    + " Hz / Snapshots: " + simulationClient.MovementSession.SnapshotRateHz + " Hz");
            }
        }

        private void BeginLeave()
        {
            if (!operationState.TryBegin(ClientOperation.LeaveShard))
            {
                return;
            }

            StartCoroutine(RunLeaveOperation());
        }

        private IEnumerator RunLeaveOperation()
        {
            yield return LeaveShardRoutine();
            operationState.Complete(ClientOperation.LeaveShard);
        }

        private IEnumerator LeaveShardRoutine()
        {
            var session = ShooterMmoClientSession.ActiveSimulationSession;
            var simulationClient = ShooterMmoClientBootstrap.SimulationClient;
            if (session == null || simulationClient == null)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                yield break;
            }

            RealtimeClientError error = null;
            yield return simulationClient.Leave(
                session.simulationSessionId,
                () => { },
                value => error = value);

            if (error != null)
            {
                SetError(error);
                if (simulationClient.IsJoined)
                {
                    yield break;
                }
            }

            ShooterMmoClientSession.ActiveSimulationSession = null;
            status = "Left shard.";
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
        }

        private void SetError(RealtimeClientError error)
        {
            status = "Error: " + error.ToDisplayMessage();
        }
    }
}
