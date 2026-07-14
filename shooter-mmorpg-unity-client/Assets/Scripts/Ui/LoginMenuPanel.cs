using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Diagnostics;
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
            GUILayout.Label("Request Timeout: " + ShooterMmoClientSession.RequestTimeoutSeconds + " seconds");
            GUILayout.Label("Realtime Timeout: " + ShooterMmoClientSession.RealtimeTimeoutSeconds + " seconds");

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
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Validating registration details for username '" + username + "'.");

            var request = new RegisterAccountRequest
            {
                email = email,
                username = username,
                password = password
            };

            yield return apiClient.Register(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                request,
                response => OnAuthSuccess(response, "registered and logged in"),
                error => SetError("Registration", error));
        }

        private IEnumerator LoginRoutine()
        {
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Validating login credentials for username '" + username + "'.");

            var request = new LoginAccountRequest
            {
                login = username,
                password = password
            };

            yield return apiClient.Login(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                request,
                response => OnAuthSuccess(response, "logged in"),
                error => SetError("Login", error));
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

        private void OnAuthSuccess(AuthResponse response, string action)
        {
            ShooterMmoClientSession.Auth = response;
            ShooterMmoClientSession.SelectedCharacter = null;
            ShooterMmoClientSession.SelectedShard = null;
            ShooterMmoClientSession.ActiveSimulationSession = null;
            status = "Authenticated as " + response.username + ".";
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Account '" + response.username + "' (" + response.accountId + ") " + action + ".");
            SceneManager.LoadScene(ShooterMmoSceneNames.CharacterSelect);
        }

        private void SetError(string operation, ShooterMmoApiError error)
        {
            status = "Error: " + error.ToDisplayMessage();
            ClientLog.Error(ClientLogCategory.Auth, operation + " failed: " + error.ToDisplayMessage());
        }
    }
}
