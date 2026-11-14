using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Text;
using TMPro;
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
        private TextCatalog texts = TextCatalog.Empty(TextTableFormat.SourceLocale);
        private string notice = string.Empty;
        private string exitNotice = string.Empty;
        private ClientSession clientSession;

        // Called by the composition root before the component is activated, with the client
        // session that a login signs in. A notice, such as that the client is out of date, shows in
        // the card and never blocks the form; so does the exit notice, why the client returned
        // here, such as a login of the account from another client.
        public void Initialize(ClientSession session, TextCatalog messageTexts, string clientNotice, string clientExitNotice)
        {
            clientSession = session;
            texts = messageTexts;
            notice = clientNotice ?? string.Empty;
            exitNotice = clientExitNotice ?? string.Empty;
        }

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
        private TMP_InputField loginField;
        private TMP_InputField logInPasswordField;
        private TMP_InputField emailField;
        private TMP_InputField usernameField;
        private TMP_InputField registerPasswordField;
        private TextMeshProUGUI status;

        public bool IsRegistering { get; private set; }

        public bool IsBusy
        {
            get { return operationState.IsBusy; }
        }

        public string StatusText
        {
            get { return status != null ? status.text : string.Empty; }
        }

        public string NoticeText
        {
            get { return notice; }
        }

        public string ExitNoticeText
        {
            get { return exitNotice; }
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
                BeginAuthentication(RegisterRoutine(), texts.Get("ui/login/creating-account"));
            }
            else
            {
                BeginAuthentication(LoginRoutine(), texts.Get("ui/login/logging-in"));
            }
        }

        private void Awake()
        {
            apiClient = new ApiClient(clientSession.Settings.RequestTimeoutSeconds);
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
            TemporaryEntryScreen.AddText(card, texts.Get("ui/login/development-client"), 14, TemporaryUiKit.TextMuted);
            if (notice.Length > 0)
            {
                TemporaryEntryScreen.AddText(card, notice, 14, TemporaryUiKit.WarningColor).gameObject.name = "Notice";
            }

            if (exitNotice.Length > 0)
            {
                TemporaryEntryScreen.AddText(card, exitNotice, 14, TemporaryUiKit.WarningColor).gameObject.name = "ExitNotice";
            }

            TemporaryEntryScreen.AddSpace(card, 8f, false);

            var tabs = TemporaryEntryScreen.AddButtonRow(card, 34f);
            logInTab = TemporaryUiKit.CreateButton(tabs, texts.Get("ui/login/log-in"), () => ShowRegistration(false), true, TemporaryButtonTone.Active);
            registerTab = TemporaryUiKit.CreateButton(tabs, texts.Get("ui/login/create-account"), () => ShowRegistration(true), true, TemporaryButtonTone.Quiet);
            TemporaryEntryScreen.AddSpace(card, 4f, false);

            var debugBuild = Debug.isDebugBuild;
            var lastLogin = AccountEntryPreferences.LastLogin;
            logInPage = TemporaryEntryScreen.AddPage(card, "LogInPage");
            loginField = screen.AddField(
                logInPage,
                texts.Get("ui/login/email-or-username"),
                lastLogin.Length > 0 ? lastLogin : (debugBuild ? DevelopmentLogin : string.Empty),
                texts.Get("ui/login/example-username"),
                TMP_InputField.ContentType.Standard,
                AccountEntryPresentation.EmailLimit,
                null);
            logInPasswordField = screen.AddField(
                logInPage,
                texts.Get("ui/login/password"),
                debugBuild ? DevelopmentPassword : string.Empty,
                texts.Get("ui/login/password"),
                TMP_InputField.ContentType.Password,
                AccountEntryPresentation.PasswordLimit,
                null);
            TemporaryEntryScreen.AddSpace(logInPage, 6f, false);
            TemporaryEntryScreen.AddMainButton(logInPage, texts.Get("ui/login/log-in"), Submit);

            registerPage = TemporaryEntryScreen.AddPage(card, "RegisterPage");
            emailField = screen.AddField(
                registerPage,
                texts.Get("ui/login/email"),
                string.Empty,
                texts.Get("ui/login/example-email"),
                TMP_InputField.ContentType.EmailAddress,
                AccountEntryPresentation.EmailLimit,
                null);
            usernameField = screen.AddField(
                registerPage,
                texts.Get("ui/login/username"),
                string.Empty,
                texts.Get("ui/login/example-username"),
                TMP_InputField.ContentType.Standard,
                AccountEntryPresentation.UsernameLimit,
                texts.Get("ui/login/username-hint"));
            registerPasswordField = screen.AddField(
                registerPage,
                texts.Get("ui/login/password"),
                debugBuild ? DevelopmentPassword : string.Empty,
                texts.Get("ui/login/password"),
                TMP_InputField.ContentType.Password,
                AccountEntryPresentation.PasswordLimit,
                texts.Get("ui/login/password-hint"));
            TemporaryEntryScreen.AddSpace(registerPage, 6f, false);
            TemporaryEntryScreen.AddMainButton(registerPage, texts.Get("ui/login/create-account"), Submit);

            status = TemporaryEntryScreen.AddStatus(card);
            BuildFooter();
        }

        // The footer: the connection, the environment and build, and, when the GameService
        // address was overridden at start, a warning line that says so. The log gets the same
        // lines, so a started player shows them without a screen.
        private void BuildFooter()
        {
            var settings = clientSession.Settings;
            var connectionText = AccountEntryPresentation.ConnectionLine(
                settings.GameServiceBaseUrl,
                settings.RequestTimeoutSeconds,
                settings.RealtimeTimeoutSeconds,
                texts);
            var buildText = AccountEntryPresentation.BuildLine(settings, texts);
            var overrideText = AccountEntryPresentation.OverrideLine(settings, texts);
            AddFooterLine("Connection", connectionText, TemporaryUiKit.TextMuted, 16f, 32f);
            AddFooterLine("Build", buildText, TemporaryUiKit.TextMuted, 48f, 24f);
            if (overrideText.Length > 0)
            {
                AddFooterLine("AddressOverride", overrideText, TemporaryUiKit.WarningColor, 72f, 24f);
            }

            ClientLog.Info(
                ClientLogCategory.Client,
                "Login screen shown. " + buildText + (overrideText.Length > 0 ? " " + overrideText : string.Empty)
                + " " + connectionText);
        }

        // A line across the bottom of the screen, from bottom up to bottom + height.
        private void AddFooterLine(string name, string text, Color color, float bottom, float height)
        {
            var line = TemporaryUiKit.AddText(
                TemporaryUiKit.CreateUiObject(name, screen.Root),
                text,
                13,
                FontStyles.Normal,
                TextAlignmentOptions.Center,
                color);
            TemporaryUiKit.Stretch(
                line.rectTransform,
                Vector2.zero,
                new Vector2(1f, 0f),
                new Vector2(16f, bottom),
                new Vector2(-16f, bottom + height));
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
                clientSession.Settings.GameServiceBaseUrl,
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
                clientSession.Settings.GameServiceBaseUrl,
                request,
                response => OnAuthSuccess(response, login, "logged in"),
                error => SetError("Login", error, logInPasswordField));
        }

        private void OnAuthSuccess(AuthResponse response, string login, string action)
        {
            clientSession.SignIn(response);
            AccountEntryPreferences.RememberLogin(login);
            SetStatus(
                texts.Format("ui/login/logged-in", MessageArgument.Text("username", response.username)),
                TemporaryUiKit.SuccessColor);
            ClientLog.Info(
                ClientLogCategory.Auth,
                "Account '" + response.username + "' (" + response.accountId + ") " + action + ".");
            ClientSceneLoader.LoadCharacterSelect();
        }

        private void SetError(string operation, ApiError error, TMP_InputField retryField)
        {
            SetStatus(
                AccountEntryPresentation.ErrorText(error, clientSession.Settings.GameServiceBaseUrl, texts),
                TemporaryUiKit.DangerColor);
            ClientLog.Error(ClientLogCategory.Auth, operation + " failed: " + error.ToDiagnostic());
            // The form is enabled again once the routine ends, so the field takes the caret then.
            StartCoroutine(FocusWhenIdle(retryField));
        }

        private IEnumerator FocusWhenIdle(TMP_InputField field)
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
