using System.Collections;
using ShooterMmo.Api;
using ShooterMmo.Config;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo
{
    public sealed class ShooterMmoClientBootstrap : MonoBehaviour
    {
        private ShooterMmoApiClient apiClient;
        private string previousSceneName;

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
            previousSceneName = SceneManager.GetActiveScene().name;
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureSceneController(SceneManager.GetActiveScene().name);
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
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

            ShooterMmoApiError error = null;
            yield return apiClient.RemoveDebugSession(
                ShooterMmoClientSession.WorldServerBaseUrl,
                session.characterId,
                session.worldSessionId,
                () =>
                {
                    if (ShooterMmoClientSession.ActiveWorldSession != null
                        && ShooterMmoClientSession.ActiveWorldSession.worldSessionId == session.worldSessionId)
                    {
                        ShooterMmoClientSession.ActiveWorldSession = null;
                    }
                },
                value => error = value);

            if (error == null)
            {
                yield break;
            }

            if (!ClientSessionRecovery.ReturnToLoginIfUnauthorized(error))
            {
                Debug.LogWarning("Fallback world leave failed: " + error.ToDisplayMessage());
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
                AddControllerIfMissing<Gameplay.WorldSceneGameplayBootstrap>();
                AddControllerIfMissing<Ui.WorldScenePanel>();
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
