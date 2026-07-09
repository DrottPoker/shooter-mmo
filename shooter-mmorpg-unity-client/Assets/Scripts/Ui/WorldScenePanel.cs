using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class WorldScenePanel : MonoBehaviour
    {
        private readonly ShooterMmoApiClient apiClient = new ShooterMmoApiClient();

        private string status = "World scene loaded.";
        private ActivePlayerSessionResponse[] serverSessions = new ActivePlayerSessionResponse[0];
        private bool isBusy;
        private Vector2 scrollPosition;

        private void Start()
        {
            RefreshServerSessions();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(620f, 620f), "World Scene", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            DrawSessionSection();
            DrawServerSection();

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(isBusy, status);

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
                GUILayout.Label("Joined At: " + session.joinedAt);
            }
            else
            {
                GUILayout.Label("No local world session. Return to CharacterSelect.");
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Server Sessions", GUILayout.Height(32f)))
            {
                RefreshServerSessions();
            }

            if (GUILayout.Button("Leave World", GUILayout.Height(32f)))
            {
                LeaveWorld();
            }

            if (GUILayout.Button("Back To Character Select", GUILayout.Height(32f)))
            {
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
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

        private void RefreshServerSessions()
        {
            RunRequest(apiClient.GetDebugSessions(
                ShooterMmoClientSession.WorldServerBaseUrl,
                result =>
                {
                    serverSessions = result;
                    status = "Loaded " + serverSessions.Length + " WorldServer sessions.";
                },
                SetError));
        }

        private void LeaveWorld()
        {
            var session = ShooterMmoClientSession.ActiveWorldSession;
            if (session == null)
            {
                ShooterMmoClientSession.ActiveWorldSession = null;
                SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                return;
            }

            RunRequest(apiClient.RemoveDebugSession(
                ShooterMmoClientSession.WorldServerBaseUrl,
                session.characterId,
                () =>
                {
                    ShooterMmoClientSession.ActiveWorldSession = null;
                    status = "Left world.";
                    SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
                },
                SetError));
        }

        private void RunRequest(System.Collections.IEnumerator request)
        {
            StartCoroutine(RunRequestRoutine(request));
        }

        private System.Collections.IEnumerator RunRequestRoutine(System.Collections.IEnumerator request)
        {
            isBusy = true;
            yield return request;
            isBusy = false;
        }

        private void SetError(string message)
        {
            status = "Error: " + message;
            Debug.LogWarning(status);
        }
    }
}

