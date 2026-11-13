using Mmorpg.Client.Core;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Worlds
{
    // The single entry point for client scene changes. LoginMenu and CharacterSelect are built
    // into the player. A World scene is an Addressables entry whose address is its scene name.
    // Addressables releases the World scene handle and its bundles when the scene unloads, so
    // returning to a built-in scene needs no explicit release.
    public static class ClientSceneLoader
    {
        public static void LoadLoginMenu()
        {
            LoadBuiltInScene(SceneNames.LoginMenu);
        }

        public static void LoadCharacterSelect()
        {
            LoadBuiltInScene(SceneNames.CharacterSelect);
        }

        // Outside Play Mode, as in EditMode tests of the ways out of a World, no scene changes.
        private static void LoadBuiltInScene(string sceneName)
        {
            if (Application.isPlaying)
            {
                SceneManager.LoadScene(sceneName);
            }
        }

        public static bool IsWorldSceneAvailable(string sceneName)
        {
            return ClientAssets.Exists(sceneName, typeof(SceneInstance));
        }

        public static AsyncOperationHandle<SceneInstance> LoadWorldScene(string sceneName)
        {
            return Addressables.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        }
    }
}
