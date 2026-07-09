using UnityEngine.SceneManagement;
using UnityEngine;

namespace ShooterMmo
{
    public sealed class ShooterMmoClientBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<ShooterMmoClientBootstrap>() != null)
            {
                return;
            }

            var gameObject = new GameObject("ShooterMmoClientBootstrap");
            DontDestroyOnLoad(gameObject);
            gameObject.AddComponent<ShooterMmoClientBootstrap>();
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
            EnsureSceneController(scene.name);
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
            if (FindFirstObjectByType<T>() != null)
            {
                return;
            }

            var gameObject = new GameObject(typeof(T).Name);
            gameObject.AddComponent<T>();
        }
    }
}
