using System.Collections;
using Mmorpg.Client.Core;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The quest windows in a loaded World without a GameService.
    public sealed class QuestWindowsPlayModeTests
    {
        // Port 9 refuses connections, so no request can reach a real GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";

        // The Equipment window stands at the left screen edge on a canvas above the quest
        // windows, so the quest dialog and the quest log stand beside it while it is open.
        [UnityTest]
        public IEnumerator TheQuestWindowsStandBesideAnOpenEquipmentWindow()
        {
            var gameService = PlayModeClient.UseGameService(UnreachableGameServiceUrl);
            PlayModeClient.Session.SignOut();
            InventoryWindows inventoryWindows = null;
            try
            {
                yield return PlayModeClient.WaitForContent();
                var worldLoad = ClientSceneLoader.LoadWorldScene(SceneNames.DevelopmentWorld1);
                yield return worldLoad;
                Assert.That(worldLoad.Status, Is.EqualTo(AsyncOperationStatus.Succeeded));
                yield return null;

                inventoryWindows = Object.FindAnyObjectByType<InventoryWindows>();
                var questWindows = Object.FindAnyObjectByType<QuestWindows>();
                Assert.That(inventoryWindows, Is.Not.Null);
                Assert.That(questWindows, Is.Not.Null);
                var theme = inventoryWindows.Theme;
                AssertLeftEdges(questWindows, theme, theme.ScreenMargin);

                inventoryWindows.SetEquipmentOpen(true);
                yield return null;
                AssertLeftEdges(
                    questWindows,
                    theme,
                    theme.ScreenMargin + inventoryWindows.Equipment.Rect.sizeDelta.x + theme.WindowGap);

                inventoryWindows.SetEquipmentOpen(false);
                yield return null;
                AssertLeftEdges(questWindows, theme, theme.ScreenMargin);
            }
            finally
            {
                if (inventoryWindows != null)
                {
                    inventoryWindows.CloseAll();
                }

                PlayModeClient.Session.SignOut();
                gameService.Dispose();
            }
        }

        private static void AssertLeftEdges(QuestWindows questWindows, UiTheme theme, float dialogLeft)
        {
            Assert.That(questWindows.Dialog.Rect.anchoredPosition.x, Is.EqualTo(dialogLeft).Within(0.01f));
            Assert.That(
                questWindows.Log.Rect.anchoredPosition.x,
                Is.EqualTo(dialogLeft + QuestWindows.DialogWidth + theme.WindowGap).Within(0.01f));
        }
    }
}
