using System.Collections;
using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class LoginMenuPanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private ShooterMmoApiClient apiClient;
        private string email = "player@example.com";
        private string username = "player_one";
        private string password = "TestPass123!";
        private string status = "Register or login to continue.";
        private Vector2 scrollPosition;

        private void Awake()
        {
            apiClient = new ShooterMmoApiClient(ShooterMmoClientSession.RequestTimeoutSeconds);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(520f, 520f), "Login Menu", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Connections");
            GUILayout.Label("Auth Service: " + ShooterMmoClientSession.AuthServiceBaseUrl);
            GUILayout.Label("World Server: " + ShooterMmoClientSession.WorldServerBaseUrl);
            GUILayout.Label("Request Timeout: " + ShooterMmoClientSession.RequestTimeoutSeconds + " seconds");

            GUILayout.Space(12f);
            GUILayout.Label("Account");
            email = TemporaryPanelStyles.LabeledTextField("Email", email);
            username = TemporaryPanelStyles.LabeledTextField("Username", username);
            password = TemporaryPanelStyles.LabeledPasswordField("Password", password);

            var previousGuiState = GUI.enabled;
            GUI.enabled = previousGuiState && !operationState.IsBusy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Register", GUILayout.Height(34f)))
            {
                BeginAuthentication(RegisterRoutine());
            }

            if (GUILayout.Button("Login", GUILayout.Height(34f)))
            {
                BeginAuthentication(LoginRoutine());
            }
            GUILayout.EndHorizontal();
            GUI.enabled = previousGuiState;

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(operationState.IsBusy, status);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private IEnumerator RegisterRoutine()
        {
            var request = new RegisterAccountRequest
            {
                email = email,
                username = username,
                password = password
            };

            yield return apiClient.Register(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                request,
                OnAuthSuccess,
                SetError);
        }

        private IEnumerator LoginRoutine()
        {
            var request = new LoginAccountRequest
            {
                login = username,
                password = password
            };

            yield return apiClient.Login(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                request,
                OnAuthSuccess,
                SetError);
        }

        private void BeginAuthentication(IEnumerator routine)
        {
            if (!operationState.TryBegin(ClientOperation.Authenticate))
            {
                return;
            }

            StartCoroutine(RunOperation(ClientOperation.Authenticate, routine));
        }

        private IEnumerator RunOperation(ClientOperation operation, IEnumerator routine)
        {
            yield return routine;
            operationState.Complete(operation);
        }

        private void OnAuthSuccess(AuthResponse response)
        {
            ShooterMmoClientSession.Auth = response;
            ShooterMmoClientSession.SelectedCharacter = null;
            ShooterMmoClientSession.SelectedWorld = null;
            ShooterMmoClientSession.ActiveWorldSession = null;
            status = "Authenticated as " + response.username + ".";
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
        }

        private void SetError(ShooterMmoApiError error)
        {
            status = "Error: " + error.ToDisplayMessage();
            Debug.LogWarning(status);
        }
    }
}
