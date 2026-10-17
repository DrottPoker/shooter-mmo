using System;
using System.Collections;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    public sealed class InventoryClientControllerPlayModeTests
    {
        // Port 9 refuses connections, so the refresh fails fast without a running GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";
        private const int MaximumListenerDepth = 8;

        [UnityTest]
        public IEnumerator ListenerThatRequestsStateDuringRefreshDoesNotStartAnotherRefresh()
        {
            var loadOperation = SceneManager.LoadSceneAsync(SceneNames.LoginMenu);
            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;
            var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(controller.State.Catalog, Is.Not.Null);

            var previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
            ClientSession.Clear();
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = Guid.NewGuid().ToString(),
                name = "Refresh Probe"
            };

            var depth = 0;
            var deepest = 0;
            void RequestStateAgain()
            {
                depth++;
                deepest = Math.Max(deepest, depth);
                if (depth < MaximumListenerDepth)
                {
                    controller.EnsureFullState();
                }

                depth--;
            }

            controller.State.Changed += RequestStateAgain;
            try
            {
                controller.EnsureFullState();
            }
            finally
            {
                controller.State.Changed -= RequestStateAgain;
            }

            // Every refresh announces itself once; a nested request is queued.
            Assert.That(deepest, Is.EqualTo(1));
            Assert.That(controller.State.Status, Is.EqualTo(InventoryClientStatus.Loading));

            var deadline = Time.realtimeSinceStartup + 30f;
            while (controller.State.Status == InventoryClientStatus.Loading
                && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            ClientSession.Clear();
            ConfigureGameServiceUrl(previousBaseUrl);
            Assert.That(controller.State.Status, Is.Not.EqualTo(InventoryClientStatus.Loading));
        }

        private static void ConfigureGameServiceUrl(string baseUrl)
        {
            var config = ScriptableObject.CreateInstance<ClientConfig>();
            JsonUtility.FromJsonOverwrite(
                "{\"gameServiceBaseUrl\":\"" + baseUrl + "\"}",
                config);
            ClientSession.Configure(config);
            UnityEngine.Object.Destroy(config);
        }
    }
}
