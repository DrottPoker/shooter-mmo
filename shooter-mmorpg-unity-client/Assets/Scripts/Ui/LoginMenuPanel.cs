using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class LoginMenuPanel : MonoBehaviour
    {
        private readonly ShooterMmoApiClient apiClient = new ShooterMmoApiClient();

        private string authServiceBaseUrl = ShooterMmoClientSession.AuthServiceBaseUrl;
        private string worldServerBaseUrl = ShooterMmoClientSession.WorldServerBaseUrl;
        private string email = "player@example.com";
        private string username = "player_one";
        private string password = "TestPass123!";
        private string status = "Register or login to continue.";
        private bool isBusy;
        private Vector2 scrollPosition;

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(520f, 520f), "Login Menu", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Connections");
            authServiceBaseUrl = TemporaryPanelStyles.LabeledTextField("Auth Service", authServiceBaseUrl);
            worldServerBaseUrl = TemporaryPanelStyles.LabeledTextField("World Server", worldServerBaseUrl);

            GUILayout.Space(12f);
            GUILayout.Label("Account");
            email = TemporaryPanelStyles.LabeledTextField("Email", email);
            username = TemporaryPanelStyles.LabeledTextField("Username", username);
            password = TemporaryPanelStyles.LabeledPasswordField("Password", password);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Register", GUILayout.Height(34f)))
            {
                Register();
            }

            if (GUILayout.Button("Login", GUILayout.Height(34f)))
            {
                Login();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(isBusy, status);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Register()
        {
            if (isBusy)
            {
                return;
            }

            var request = new RegisterAccountRequest
            {
                email = email,
                username = username,
                password = password
            };

            RunRequest(apiClient.Register(authServiceBaseUrl, request, OnAuthSuccess, SetError));
        }

        private void Login()
        {
            if (isBusy)
            {
                return;
            }

            var request = new LoginAccountRequest
            {
                login = username,
                password = password
            };

            RunRequest(apiClient.Login(authServiceBaseUrl, request, OnAuthSuccess, SetError));
        }

        private void OnAuthSuccess(AuthResponse response)
        {
            ShooterMmoClientSession.AuthServiceBaseUrl = authServiceBaseUrl;
            ShooterMmoClientSession.WorldServerBaseUrl = worldServerBaseUrl;
            ShooterMmoClientSession.Auth = response;
            ShooterMmoClientSession.SelectedCharacter = null;
            ShooterMmoClientSession.SelectedWorld = null;
            ShooterMmoClientSession.ActiveWorldSession = null;
            status = "Authenticated as " + response.username + ".";
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
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

