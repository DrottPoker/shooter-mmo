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
        // A refused connection takes up to about 2 s to fail on Windows.
        private const float RefusedRequestSeconds = 5f;

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

        // A refresh that still waits for its response when the player selects another character
        // ends without touching the next character's state, so its failure shows no error there.
        [UnityTest]
        public IEnumerator ARefreshOfThePreviousCharacterNeverReachesTheNextOne()
        {
            var loadOperation = SceneManager.LoadSceneAsync(SceneNames.LoginMenu);
            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;
            var controller = UnityEngine.Object.FindAnyObjectByType<InventoryClientController>();
            Assert.That(controller, Is.Not.Null);
            var previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
            ClientSession.Clear();
            ClientSession.Auth = new AuthResponse { accountId = Guid.NewGuid().ToString() };
            ClientSession.SelectedCharacter = new CharacterResponse
            {
                id = Guid.NewGuid().ToString(),
                name = "First Probe"
            };
            try
            {
                // The controller observes the selected character on its next update.
                yield return null;
                controller.EnsureFullState();
                Assert.That(controller.State.Status, Is.EqualTo(InventoryClientStatus.Loading));

                var next = Guid.NewGuid();
                ClientSession.SelectedCharacter = new CharacterResponse
                {
                    id = next.ToString(),
                    name = "Next Probe"
                };
                yield return null;
                Assert.That(controller.State.CharacterId, Is.EqualTo(next));
                var status = controller.State.Status;

                // Long enough for the refused request of the first character to fail.
                var deadline = Time.realtimeSinceStartup + RefusedRequestSeconds;
                while (controller.State.Error == null && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(controller.State.Error, Is.Null);
                Assert.That(controller.State.Status, Is.EqualTo(status));
                Assert.That(controller.State.CharacterId, Is.EqualTo(next));
            }
            finally
            {
                ClientSession.Clear();
                ConfigureGameServiceUrl(previousBaseUrl);
            }
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
