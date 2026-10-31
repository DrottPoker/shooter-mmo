using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Ui
{
    // The temporary LoginMenu screen: one card with a Log In and a Create Account tab, and the
    // GameService connection below it. Log In takes an email or a username; it starts with the
    // last login name of this computer and puts the caret in the password. Tab moves between the
    // fields and Enter sends the open tab. A failed request shows its reason in the card.
    public sealed class LoginMenuPanel : MonoBehaviour
    {
        public const string ScreenName = "LoginMenu";

        // The local development account, filled in only in the Editor and development builds.
        private const string DevelopmentLogin = "player_one";
        private const string DevelopmentPassword = "TestPass123!";

        private readonly ClientOperationState operationState = new ClientOperationState();

        private ApiClient apiClient;
        private TemporaryEntryScreen screen;
        private CanvasGroup form;
        private Button logInTab;
        private Button registerTab;
        private RectTransform logInPage;
        private RectTransform registerPage;
        private InputField loginField;
        private InputField logInPasswordField;
        private InputField emailField;
        private InputField usernameField;
        private InputField registerPasswordField;
        private Text status;

        public bool IsRegistering { get; private set; }

        public bool IsBusy
        {
            get { return operationState.IsBusy; }
        }

        public string StatusText
        {
            get { return status != null ? status.text : string.Empty; }
        }

        // Shows the Create Account tab, or the Log In tab.
        public void ShowRegistration(bool registering)
        {
            IsRegistering = registering;
            if (form == null)
            {
                return;
            }

            logInPage.gameObject.SetActive(!registering);
            registerPage.gameObject.SetActive(registering);
            TemporaryUiKit.SetButtonTone(logInTab, registering ? TemporaryButtonTone.Quiet : TemporaryButtonTone.Active);
            TemporaryUiKit.SetButtonTone(registerTab, registering ? TemporaryButtonTone.Active : TemporaryButtonTone.Quiet);
            SetStatus(string.Empty, TemporaryUiKit.TextSecondary);
            FocusFirstEmpty();
        }

        // Sends the open tab: a login, or a registration that also logs in.
        public void Submit()
        {
            if (IsRegistering)
            {
                BeginAuthentication(RegisterRoutine(), "Creating account...");
            }
            else
            {
                BeginAuthentication(LoginRoutine(), "Logging in...");
            }
        }

        private void Awake()
        {
            apiClient = new ApiClient(ClientSession.RequestTimeoutSeconds);
        }

        private void Start()
        {
            BuildUi();
            ShowRegistration(false);
        }

        private void OnDestroy()
        {
            apiClient?.Dispose();
            screen?.Destroy();
        }

        private void Update()
        {
            if (screen != null && screen.ReadKeys() && !operationState.IsBusy)
            {
                Submit();
            }
        }

        private void BuildUi()
        {
            screen = TemporaryEntryScreen.Create(transform, ScreenName);
            var card = TemporaryEntryScreen.AddCard(screen.Root, "LoginCard", new Vector2(0f, 260f), new Vector2(440f, 0f), true);
            form = card.gameObject.AddComponent<CanvasGroup>();
            TemporaryEntryScreen.AddHeading(card, TemporaryEntryScreen.GameTitle, 30);
            TemporaryEntryScreen.AddText(card, "Development client", 14, TemporaryUiKit.TextMuted);
            TemporaryEntryScreen.AddSpace(card, 8f, false);

            var tabs = TemporaryEntryScreen.AddButtonRow(card, 34f);
            logInTab = TemporaryUiKit.CreateButton(tabs, "Log In", () => ShowRegistration(false), true, TemporaryButtonTone.Active);
            registerTab = TemporaryUiKit.CreateButton(tabs, "Create Account", () => ShowRegistration(true), true, TemporaryButtonTone.Quiet);
            TemporaryEntryScreen.AddSpace(card, 4f, false);

            var debugBuild = Debug.isDebugBuild;
            var lastLogin = AccountEntryPreferences.LastLogin;
            logInPage = TemporaryEntryScreen.AddPage(card, "LogInPage");
            loginField = screen.AddField(
                logInPage,
                "Email or username",
                lastLogin.Length > 0 ? lastLogin : (debugBuild ? DevelopmentLogin : string.Empty),
                "player_one",
                InputField.ContentType.Standard,
                AccountEntryPresentation.EmailLimit,
                null);
            logInPasswordField = screen.AddField(
                logInPage,
                "Password",
                debugBuild ? DevelopmentPassword : string.Empty,
                "Password",
                InputField.ContentType.Password,
                AccountEntryPresentation.PasswordLimit,
                null);
            TemporaryEntryScreen.AddSpace(logInPage, 6f, false);
            TemporaryEntryScreen.AddMainButton(logInPage, "Log In", Submit);

            registerPage = TemporaryEntryScreen.AddPage(card, "RegisterPage");
            emailField = screen.AddField(
                registerPage,
                "Email",
                string.Empty,
                "you@example.com",
                InputField.ContentType.EmailAddress,
                AccountEntryPresentation.EmailLimit,
                null);
            usernameField = screen.AddField(
                registerPage,
                "Username",
                string.Empty,
                "player_one",
                InputField.ContentType.Standard,
                AccountEntryPresentation.UsernameLimit,
                AccountEntryPresentation.UsernameHint);
            registerPasswordField = screen.AddField(
                registerPage,
                "Password",
                debugBuild ? DevelopmentPassword : string.Empty,
                "Password",
                InputField.ContentType.Password,
                AccountEntryPresentation.PasswordLimit,
                AccountEntryPresentation.PasswordHint);
            TemporaryEntryScreen.AddSpace(registerPage, 6f, false);
            TemporaryEntryScreen.AddMainButton(registerPage, "Create Account", Submit);

            status = TemporaryEntryScreen.AddStatus(card);

            var connection = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject("Connection", screen.Root),
                AccountEntryPresentation.ConnectionLine(
                    ClientSession.GameServiceBaseUrl,
                    ClientSession.RequestTimeoutSeconds,
                    ClientSession.RealtimeTimeoutSeconds),
                13,
                FontStyle.Normal,
                TextAnchor.MiddleCenter,
                TemporaryUiKit.TextMuted);
            TemporaryUiKit.Stretch(connection.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(16f, 16f), new Vector2(-16f, 48f));
        }

        private void FocusFirstEmpty()
        {
            if (IsRegistering)
            {
                TemporaryEntryScreen.Focus(emailField.text.Length == 0
                    ? emailField
                    : usernameField.text.Length == 0 ? usernameField : registerPasswordField);
            }
            else
            {
                TemporaryEntryScreen.Focus(loginField.text.Length == 0 ? loginField : logInPasswordField);
            }
        }

        private void BeginAuthentication(IEnumerator routine, string progress)
        {
            if (!operationState.TryBegin(ClientOperation.Authenticate))
            {
                return;
            }

            SetStatus(progress, TemporaryUiKit.TextSecondary);
            SetBusy(true);
            StartCoroutine(RunOperation(ClientOperation.Authenticate, routine));
        }

        private IEnumerator RunOperation(ClientOperation operation, IEnumerator routine)
        {
            yield return routine;
            operationState.Complete(operation);
            SetBusy(false);
        }

        private IEnumerator RegisterRoutine()
        {
            var username = usernameField.text.Trim();
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Validating registration details for username '" + username + "'.");

            var request = new RegisterAccountRequest
            {
                email = emailField.text.Trim(),
                username = username,
                password = registerPasswordField.text
            };

            yield return apiClient.Register(
                ClientSession.GameServiceBaseUrl,
                request,
                response => OnAuthSuccess(response, username, "registered and logged in"),
                error => SetError("Registration", error, registerPasswordField));
        }

        private IEnumerator LoginRoutine()
        {
            var login = loginField.text.Trim();
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Validating login credentials for username '" + login + "'.");

            var request = new LoginAccountRequest
            {
                login = login,
                password = logInPasswordField.text
            };

            yield return apiClient.Login(
                ClientSession.GameServiceBaseUrl,
                request,
                response => OnAuthSuccess(response, login, "logged in"),
                error => SetError("Login", error, logInPasswordField));
        }

        private void OnAuthSuccess(AuthResponse response, string login, string action)
        {
            ClientSession.Auth = response;
            ClientSession.SelectedCharacter = null;
            ClientSession.SelectedShard = null;
            ClientSession.ActiveSimulationSession = null;
            AccountEntryPreferences.RememberLogin(login);
            SetStatus("Logged in as " + response.username + ". Loading characters...", TemporaryUiKit.SuccessColor);
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Account '" + response.username + "' (" + response.accountId + ") " + action + ".");
            ClientSceneLoader.LoadCharacterSelect();
        }

        private void SetError(string operation, ApiError error, InputField retryField)
        {
            SetStatus(AccountEntryPresentation.ErrorText(error, ClientSession.GameServiceBaseUrl), TemporaryUiKit.DangerColor);
            ClientLog.Error(ClientLogCategory.Auth, operation + " failed: " + error.ToDisplayMessage());
            // The form is enabled again once the routine ends, so the field takes the caret then.
            StartCoroutine(FocusWhenIdle(retryField));
        }

        private IEnumerator FocusWhenIdle(InputField field)
        {
            while (operationState.IsBusy)
            {
                yield return null;
            }

            TemporaryEntryScreen.Focus(field);
        }

        private void SetBusy(bool busy)
        {
            if (form != null)
            {
                form.interactable = !busy;
            }
        }

        private void SetStatus(string text, Color color)
        {
            if (status == null)
            {
                return;
            }

            status.text = text ?? string.Empty;
            status.color = color;
        }
    }
}
