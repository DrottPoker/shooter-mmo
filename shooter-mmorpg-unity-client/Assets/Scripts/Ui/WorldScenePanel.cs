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

        private string status = "Realtime world connection active.";
        private Vector2 scrollPosition;
        private LocalPlayerInput localPlayerInput;
        private bool isVisible = true;

        private void Start()
        {
            localPlayerInput = FindAnyObjectByType<LocalPlayerInput>();
            if (ShooterMmoClientSession.ActiveWorldSession == null)
            {
#if UNITY_EDITOR
                status = "Offline Editor movement preview. Realtime is disconnected.";
                return;
#else
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                return;
#endif
            }

            if (ShooterMmoClientBootstrap.WorldClient == null
                || !ShooterMmoClientBootstrap.WorldClient.IsJoined)
            {
                ShooterMmoClientSession.ActiveWorldSession = null;
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

            var session = ShooterMmoClientSession.ActiveWorldSession;
            if (session != null)
            {
                GUILayout.Label("Character: " + session.characterName);
                GUILayout.Label("World: " + session.worldId);
                GUILayout.Label("Session: " + session.worldSessionId);
                GUILayout.Label("Reconnect: " + session.isReconnect);
            }
            else
            {
                GUILayout.Label("No local world session.");
            }

            if (GUILayout.Button("Leave World", GUILayout.Height(28f)))
            {
                BeginLeave();
            }

            GUILayout.Space(6f);
        }

        private void DrawTransportSection()
        {
            GUILayout.Label("Realtime Transport");
            var worldClient = ShooterMmoClientBootstrap.WorldClient;
            if (worldClient == null)
            {
                GUILayout.Label("Client unavailable");
                return;
            }

            GUILayout.Label("State: " + worldClient.State);
            GUILayout.Label("Endpoint: " + worldClient.ConnectedHost + ":" + worldClient.ConnectedPort + "/udp");
            if (worldClient.MovementSession != null)
            {
                GUILayout.Label("Authority: WorldServer");
                GUILayout.Label("Server Tick: " + worldClient.LatestServerTick);
                GUILayout.Label(
                    "Simulation: " + worldClient.MovementSession.Settings.TickRateHz
                    + " Hz / Snapshots: " + worldClient.MovementSession.SnapshotRateHz + " Hz");
            }
        }

        private void BeginLeave()
        {
            if (!operationState.TryBegin(ClientOperation.LeaveWorld))
            {
                return;
            }

            StartCoroutine(RunLeaveOperation());
        }

        private IEnumerator RunLeaveOperation()
        {
            yield return LeaveWorldRoutine();
            operationState.Complete(ClientOperation.LeaveWorld);
        }

        private IEnumerator LeaveWorldRoutine()
        {
            var session = ShooterMmoClientSession.ActiveWorldSession;
            var worldClient = ShooterMmoClientBootstrap.WorldClient;
            if (session == null || worldClient == null)
            {
                ShooterMmoClientSession.ActiveWorldSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                yield break;
            }

            RealtimeClientError error = null;
            yield return worldClient.Leave(
                session.worldSessionId,
                () => { },
                value => error = value);

            if (error != null)
            {
                SetError(error);
                if (worldClient.IsJoined)
                {
                    yield break;
                }
            }

            ShooterMmoClientSession.ActiveWorldSession = null;
            status = "Left world.";
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
        }

        private void SetError(RealtimeClientError error)
        {
            status = "Error: " + error.ToDisplayMessage();
        }
    }
}
