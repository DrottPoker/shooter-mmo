using System.Collections;
using System.Linq;
using Mmorpg.Client.App;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.Client.WorldActors;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    public sealed class ClientBootstrapPlayModeTests
    {
        [UnityTest]
        public IEnumerator LoadingLoginSceneCreatesTheRuntimeLoginPanel()
        {
            var loadOperation = SceneManager.LoadSceneAsync(SceneNames.LoginMenu);
            Assert.That(loadOperation, Is.Not.Null);

            while (!loadOperation.isDone)
            {
                yield return null;
            }

            yield return null;

            Assert.That(Object.FindAnyObjectByType<ClientBootstrap>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<RealtimeSimulationClient>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<InventoryClientController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<CorpseClientController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<WorldActorClientController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<WorldInteractionClientController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<InventoryClientController>().IsInitialized, Is.True);
            Assert.That(Object.FindAnyObjectByType<LoginMenuPanel>(), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator LoadingWorldSceneWithoutJoinedSessionDoesNotSpawnLocalPlayer()
        {
            ClientSession.Clear();
            var loadOperation = SceneManager.LoadSceneAsync(
                SceneNames.DevelopmentWorld1);
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
            Assert.That(Object.FindAnyObjectByType<CorpsePresentationController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<WorldActorPresentationController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<WorldInteractionTargetingController>(), Is.Not.Null);
            Assert.That(Object.FindAnyObjectByType<TemporaryWorldInteractionPanel>(), Is.Not.Null);
            var inventoryPanel = Object.FindAnyObjectByType<TemporaryInventoryPanel>();
            Assert.That(inventoryPanel, Is.Not.Null);
            Assert.That(inventoryPanel.IsOpen, Is.False);

            inventoryPanel.SetOpen(true);
            yield return null;
            Assert.That(inventoryPanel.IsOpen, Is.True);
            Assert.That(inventoryPanel.Mode, Is.EqualTo(InventoryPanelMode.FullDevelopment));
            Assert.That(inventoryPanel.IsEquipmentModuleVisible, Is.True);
            Assert.That(inventoryPanel.IsContextModuleVisible, Is.True);
            Assert.That(inventoryPanel.IsCharacterModuleVisible, Is.True);
            var characterPanel = inventoryPanel
                .GetComponentsInChildren<RectTransform>(true)
                .Single(rectTransform => rectTransform.name == "Character InventoryPanel");
            var characterAnchorMin = characterPanel.anchorMin;
            var characterAnchorMax = characterPanel.anchorMax;
            Assert.That(
                inventoryPanel.GetComponentInChildren<Canvas>(true),
                Is.Not.Null);
            inventoryPanel.SetOpen(true, InventoryPanelMode.CharacterOnly);
            yield return null;
            Assert.That(inventoryPanel.Mode, Is.EqualTo(InventoryPanelMode.CharacterOnly));
            Assert.That(inventoryPanel.IsEquipmentModuleVisible, Is.False);
            Assert.That(inventoryPanel.IsContextModuleVisible, Is.False);
            Assert.That(inventoryPanel.IsCharacterModuleVisible, Is.True);
            Assert.That(characterPanel.anchorMin, Is.EqualTo(characterAnchorMin));
            Assert.That(characterPanel.anchorMax, Is.EqualTo(characterAnchorMax));
            inventoryPanel.SetOpen(true, InventoryPanelMode.CharacterAndEquipment);
            yield return null;
            Assert.That(inventoryPanel.Mode, Is.EqualTo(InventoryPanelMode.CharacterAndEquipment));
            Assert.That(inventoryPanel.IsEquipmentModuleVisible, Is.True);
            Assert.That(inventoryPanel.IsContextModuleVisible, Is.False);
            Assert.That(inventoryPanel.IsCharacterModuleVisible, Is.True);
            Assert.That(characterPanel.anchorMin, Is.EqualTo(characterAnchorMin));
            Assert.That(characterPanel.anchorMax, Is.EqualTo(characterAnchorMax));
            inventoryPanel.SetOpen(false);
            Assert.That(inventoryPanel.IsOpen, Is.False);
        }
    }
}
