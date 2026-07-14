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

        public static RealtimeSimulationClient SimulationClient { get; private set; }

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
            SimulationClient = GetComponent<RealtimeSimulationClient>();
            if (SimulationClient == null)
            {
                SimulationClient = gameObject.AddComponent<RealtimeSimulationClient>();
            }

            SimulationClient.UnexpectedlyDisconnected += OnUnexpectedlyDisconnected;
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
            if (SimulationClient != null)
            {
                SimulationClient.UnexpectedlyDisconnected -= OnUnexpectedlyDisconnected;
            }

            SimulationClient = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (previousSceneName == ShooterMmoSceneNames.WorldScene
                && scene.name != ShooterMmoSceneNames.WorldScene
                && ShooterMmoClientSession.ActiveSimulationSession != null)
            {
                StartCoroutine(ReleaseAbandonedSimulationSessionRoutine());
            }

            previousSceneName = scene.name;
            EnsureSceneController(scene.name);
        }

        private IEnumerator ReleaseAbandonedSimulationSessionRoutine()
        {
            var session = ShooterMmoClientSession.ActiveSimulationSession;
            if (session == null)
            {
                yield break;
            }

            RealtimeClientError error = null;
            if (SimulationClient != null && SimulationClient.IsJoined)
            {
                yield return SimulationClient.Leave(
                    session.simulationSessionId,
                    () => { },
                    value => error = value);
            }

            if (error != null)
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Fallback shard leave did not complete. Closing UDP locally: " + error.ToDisplayMessage());
                if (SimulationClient != null)
                {
                    SimulationClient.Abort();
                }
            }

            if (ShooterMmoClientSession.ActiveSimulationSession != null
                && ShooterMmoClientSession.ActiveSimulationSession.simulationSessionId == session.simulationSessionId)
            {
                ShooterMmoClientSession.ActiveSimulationSession = null;
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
                "The active simulation connection closed. Clearing local shard state and leaving WorldScene: "
                + error.ToDisplayMessage());
            ShooterMmoClientSession.ActiveSimulationSession = null;

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
