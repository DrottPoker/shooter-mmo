using System.Collections;
using NUnit.Framework;
using ShooterMmo.Gameplay;
using ShooterMmo.Items;
using ShooterMmo.Networking;
using ShooterMmo.Ui;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShooterMmo.Tests.PlayMode
{
    public sealed class ClientBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator LoadingLoginSceneCreatesTheRuntimeLoginPanel()
        {
            var loadOperation = SceneManager.LoadSceneAsync(ShooterMmoSceneNames.LoginMenu);
            Assert.That(loadOperation, Is.Not.Null);

            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;

            Assert.That(Object.FindAnyObjectByType<ShooterMmoClientBootstrap>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<RealtimeSimulationClient>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<InventoryClientController>(), Is.Not.Null);
            Assert.That(ShooterMmoClientBootstrap.InventoryController.IsInitialized, Is.True);
            Assert.That(Object.FindAnyObjectByType<LoginMenuPanel>(), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator LoadingWorldSceneWithoutJoinedSessionDoesNotSpawnLocalPlayer()
        {
            ShooterMmoClientSession.Clear();
            var loadOperation = SceneManager.LoadSceneAsync(ShooterMmoSceneNames.WorldScene);
            Assert.That(loadOperation, Is.Not.Null);

            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;

            var context = Object.FindAnyObjectByType<WorldSceneContext>();
            Assert.That(context, Is.Not.Null);
            Assert.That(context.enabled, Is.False);
            Assert.That(Object.FindAnyObjectByType<LocalPlayerController>(), Is.Null);
            var inventoryPanel = Object.FindAnyObjectByType<TemporaryInventoryPanel>();
            Assert.That(inventoryPanel, Is.Not.Null);
            Assert.That(inventoryPanel.IsOpen, Is.False);

            inventoryPanel.SetOpen(true);
            yield return null;
            Assert.That(inventoryPanel.IsOpen, Is.True);
            Assert.That(
                inventoryPanel.GetComponentInChildren<Canvas>(true),
                Is.Not.Null);
            inventoryPanel.SetOpen(false);
            Assert.That(inventoryPanel.IsOpen, Is.False);
        }
    }
}
