using System.Collections;
using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class WorldScenePanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private ShooterMmoApiClient apiClient;
        private string status = "World scene loaded.";
        private ActivePlayerSessionResponse[] serverSessions = new ActivePlayerSessionResponse[0];
        private Vector2 scrollPosition;

        private void Awake()
        {
            apiClient = new ShooterMmoApiClient(ShooterMmoClientSession.RequestTimeoutSeconds);
        }

        private void Start()
        {
            BeginOperation(ClientOperation.RefreshWorldSession, RefreshServerSessionsRoutine());
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(620f, 620f), "World Scene", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            var previousGuiState = GUI.enabled;
            GUI.enabled = previousGuiState && !operationState.IsBusy;
            DrawSessionSection();
            DrawServerSection();
            GUI.enabled = previousGuiState;

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(operationState.IsBusy, status);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawSessionSection()
        {
            GUILayout.Label("Local Session");

            if (ShooterMmoClientSession.ActiveWorldSession != null)
            {
                var session = ShooterMmoClientSession.ActiveWorldSession;
                GUILayout.Label("Character: " + session.characterName);
                GUILayout.Label("Character Id: " + session.characterId);
                GUILayout.Label("World: " + session.worldId);
                GUILayout.Label("World Session Id: " + session.worldSessionId);
                GUILayout.Label("Joined At: " + session.joinedAt);
                GUILayout.Label("Session Expires At: " + session.sessionExpiresAt);
                GUILayout.Label("Reconnect: " + session.isReconnect);
            }
            else
            {
                GUILayout.Label("No local world session.");
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Server Sessions", GUILayout.Height(32f)))
            {
                BeginOperation(ClientOperation.RefreshWorldSession, RefreshServerSessionsRoutine());
            }

            if (GUILayout.Button("Leave World", GUILayout.Height(32f)))
            {
                BeginLeave(ShooterMmoSceneNames.CharacterSelect, clearAuthentication: false);
            }

            if (GUILayout.Button("Back To Character Select", GUILayout.Height(32f)))
            {
                BeginLeave(ShooterMmoSceneNames.CharacterSelect, clearAuthentication: false);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(12f);
        }

        private void DrawServerSection()
        {
            GUILayout.Label("WorldServer Sessions");

            if (serverSessions.Length == 0)
            {
                GUILayout.Label("No active server sessions loaded.");
                return;
            }

            for (var index = 0; index < serverSessions.Length; index++)
            {
                var session = serverSessions[index];
                GUILayout.Label(session.characterName + " on " + session.worldId + " (" + session.characterId + ")");
            }
        }

        private IEnumerator RefreshServerSessionsRoutine()
        {
            ActivePlayerSessionResponse[] result = null;
            ShooterMmoApiError error = null;
            yield return apiClient.GetDebugSessions(
                ShooterMmoClientSession.WorldServerBaseUrl,
                value => result = value,
                value => error = value);

            if (error == null)
            {
                serverSessions = result ?? new ActivePlayerSessionResponse[0];
                status = "Loaded " + serverSessions.Length + " WorldServer sessions.";
                yield break;
            }

            if (error.IsUnauthorized)
            {
                yield return LeaveWorldRoutine(ShooterMmoSceneNames.LoginMenu, clearAuthentication: true);
                yield break;
            }

            SetError(error);
        }

        private void BeginLeave(string destinationScene, bool clearAuthentication)
        {
            BeginOperation(
                ClientOperation.LeaveWorld,
                LeaveWorldRoutine(destinationScene, clearAuthentication));
        }

        private IEnumerator LeaveWorldRoutine(string destinationScene, bool clearAuthentication)
        {
            var session = ShooterMmoClientSession.ActiveWorldSession;
            if (session != null)
            {
                ShooterMmoApiError error = null;
                yield return apiClient.RemoveDebugSession(
                    ShooterMmoClientSession.WorldServerBaseUrl,
                    session.characterId,
                    session.worldSessionId,
                    () => { },
                    value => error = value);

                if (error != null && error.IsUnauthorized)
                {
                    clearAuthentication = true;
                    destinationScene = ShooterMmoSceneNames.LoginMenu;
                }
                else if (error != null)
                {
                    SetError(error);
                    yield break;
                }
            }

            ShooterMmoClientSession.ActiveWorldSession = null;
            if (clearAuthentication)
            {
                ShooterMmoClientSession.Clear();
            }

            status = "Left world.";
            SceneManager.LoadScene(destinationScene);
        }

        private void BeginOperation(ClientOperation operation, IEnumerator routine)
        {
            if (!operationState.TryBegin(operation))
            {
                return;
            }

            StartCoroutine(RunOperation(operation, routine));
        }

        private IEnumerator RunOperation(ClientOperation operation, IEnumerator routine)
        {
            yield return routine;
            operationState.Complete(operation);
        }

        private void SetError(ShooterMmoApiError error)
        {
            status = "Error: " + error.ToDisplayMessage();
            Debug.LogWarning(status);
        }
    }
}
