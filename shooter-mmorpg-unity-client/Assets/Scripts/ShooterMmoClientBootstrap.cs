using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Config;
using ShooterMmo.Diagnostics;
using ShooterMmo.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo
{
    public sealed class ShooterMmoClientBootstrap : MonoBehaviour
    {
        private string previousSceneName;
        private ShooterMmoApiClient apiClient;
        private Coroutine accountSessionMonitor;

        public static RealtimeWorldClient WorldClient { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            ShooterMmoClientSession.Configure(ShooterMmoClientConfig.Load());

            if (FindAnyObjectByType<ShooterMmoClientBootstrap>() != null)
            {
                return;
            }

            var gameObject = new GameObject("ShooterMmoClientBootstrap");
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<ShooterMmoClientBootstrap>();
        }

        private void Awake()
        {
            apiClient = new ShooterMmoApiClient(ShooterMmoClientSession.RequestTimeoutSeconds);
            WorldClient = GetComponent<RealtimeWorldClient>();
            if (WorldClient == null)
            {
                WorldClient = gameObject.AddComponent<RealtimeWorldClient>();
            }

            WorldClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
            previousSceneName = SceneManager.GetActiveScene().name;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureSceneController(SceneManager.GetActiveScene().name);
            accountSessionMonitor = StartCoroutine(MonitorAccountSessionRoutine());
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (accountSessionMonitor != null)
            {
                StopCoroutine(accountSessionMonitor);
                accountSessionMonitor = null;
            }
        }

        private void OnDestroy()
        {
            if (WorldClient != null)
            {
                WorldClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            }

            WorldClient = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (previousSceneName == ShooterMmoSceneNames.WorldScene
                && scene.name != ShooterMmoSceneNames.WorldScene
                && ShooterMmoClientSession.ActiveWorldSession != null)
            {
                StartCoroutine(ReleaseAbandonedWorldSessionRoutine());
            }

            previousSceneName = scene.name;
            EnsureSceneController(scene.name);
        }

        private IEnumerator ReleaseAbandonedWorldSessionRoutine()
        {
            var session = ShooterMmoClientSession.ActiveWorldSession;
            if (session == null)
            {
                yield break;
            }

            RealtimeClientError error = null;
            if (WorldClient != null && WorldClient.IsJoined)
            {
                yield return WorldClient.Leave(
                    session.worldSessionId,
                    () => { },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback world leave did not complete. Closing UDP locally: " + error.ToDisplayMessage());
                if (WorldClient != null)
                {
                    WorldClient.Abort();
                }
            }

            if (ShooterMmoClientSession.ActiveWorldSession != null
                && ShooterMmoClientSession.ActiveWorldSession.worldSessionId == session.worldSessionId)
            {
                ShooterMmoClientSession.ActiveWorldSession = null;
            }
        }

        private void OnUnexpectedlyDisconnected(RealtimeClientError error)
        {
            if (string.Equals(
                error.Code,
                ClientSessionRecovery.AccountSessionReplacedCode,
                System.StringComparison.Ordinal))
            {
                ClientLog.Error(
                    ClientLogCategory.Auth,
                    "This client was disconnected because the account logged in from another client. "
                    + error.ToDisplayMessage());
                ShooterMmoClientSession.Clear();
                if (SceneManager.GetActiveScene().name != ShooterMmoSceneNames.LoginMenu)
                {
                    SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
                }

                return;
            }

            ClientLog.Warning(
                ClientLogCategory.Client,
                "The active world connection closed. Clearing local world state and leaving WorldScene: "
                + error.ToDisplayMessage());
            ShooterMmoClientSession.ActiveWorldSession = null;

            if (SceneManager.GetActiveScene().name == ShooterMmoSceneNames.WorldScene)
            {
                SceneManager.LoadScene(ShooterMmoClientSession.IsAuthenticated
                    ? ShooterMmoSceneNames.CharacterSelect
                    : ShooterMmoSceneNames.LoginMenu);
            }
        }

        private IEnumerator MonitorAccountSessionRoutine()
        {
            var wait = new WaitForSecondsRealtime(
                ShooterMmoClientSession.SessionValidationIntervalSeconds);
            while (true)
            {
                yield return wait;

                var token = ShooterMmoClientSession.SessionToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                ShooterMmoApiError error = null;
                yield return apiClient.ValidateSession(
                    ShooterMmoClientSession.AuthServiceBaseUrl,
                    token,
                    () => { },
                    value => error = value);

                if (!string.Equals(
                    token,
                    ShooterMmoClientSession.SessionToken,
                    System.StringComparison.Ordinal))
                {
                    continue;
                }

                ClientSessionRecovery.ReturnToLoginIfUnauthorized(error);
            }
        }

        private static void EnsureSceneController(string sceneName)
        {
            if (sceneName == ShooterMmoSceneNames.LoginMenu)
            {
                AddControllerIfMissing<Ui.LoginMenuPanel>();
                return;
            }

            if (sceneName == ShooterMmoSceneNames.CharacterSelect)
            {
                AddControllerIfMissing<Ui.CharacterSelectPanel>();
                return;
            }

            if (sceneName == ShooterMmoSceneNames.WorldScene)
            {
                AddControllerIfMissing<Gameplay.CrosshairController>();
                AddControllerIfMissing<Ui.WorldScenePanel>();
                AddControllerIfMissing<Ui.CrosshairPanel>();
            }
        }

        private static void AddControllerIfMissing<T>() where T : Component
        {
            if (FindAnyObjectByType<T>() != null)
            {
                return;
            }

            var gameObject = new GameObject(typeof(T).Name);
            gameObject.AddComponent<T>();
        }
    }
}
