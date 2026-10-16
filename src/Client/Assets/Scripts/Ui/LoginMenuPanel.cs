using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    public sealed class LoginMenuPanel : MonoBehaviour
    {
        private readonly ClientOperationState operationState = new ClientOperationState();

        private ApiClient apiClient;
        private string email = "player@example.com";
        private string username = "player_one";
        private string password = "TestPass123!";
        private string status = "Register or login to continue.";
        private Vector2 scrollPosition;

        private void OnDestroy()
        {
            apiClient?.Dispose();
        }

        private void Awake()
        {
            apiClient = new ApiClient(ClientSession.RequestTimeoutSeconds);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(520f, 520f), "Login Menu", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            GUILayout.Label("Connections");
            GUILayout.Label("Game Service: " + ClientSession.GameServiceBaseUrl);
            GUILayout.Label("Request Timeout: " + ClientSession.RequestTimeoutSeconds + " seconds");
            GUILayout.Label("Realtime Timeout: " + ClientSession.RealtimeTimeoutSeconds + " seconds");

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
                ClientSession.GameServiceBaseUrl,
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
                ClientSession.GameServiceBaseUrl,
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
            ClientSession.Auth = response;
            ClientSession.SelectedCharacter = null;
            ClientSession.SelectedShard = null;
            ClientSession.ActiveSimulationSession = null;
            status = "Authenticated as " + response.username + ".";
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Account '" + response.username + "' (" + response.accountId + ") " + action + ".");
            ClientSceneLoader.LoadCharacterSelect();
        }

        private void SetError(string operation, ApiError error)
        {
            status = "Error: " + error.ToDisplayMessage();
            ClientLog.Error(ClientLogCategory.Auth, operation + " failed: " + error.ToDisplayMessage());
        }
    }
}
