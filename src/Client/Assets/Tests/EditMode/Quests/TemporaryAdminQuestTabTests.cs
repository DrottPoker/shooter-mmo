using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Quests;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.EditMode
{
    // The Admin menu's Quests tab lists every quest of the catalog with the character's state and
    // offers the development tools that fit it.
    public sealed class TemporaryAdminQuestTabTests
    {
        private static readonly Guid CharacterId = Guid.Parse("c0000000-0000-4000-8000-000000000002");
        private GameObject canvasObject;
        private GameObject controllerObject;

        [SetUp]
        public void SetUp()
        {
            canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            controllerObject = new GameObject("Quests");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(canvasObject);
            UnityEngine.Object.DestroyImmediate(controllerObject);
        }

        [Test]
        public void EveryQuestShowsItsStateAndTheToolsThatFitIt()
        {
            var page = TemporaryUiKit.CreateUiObject("QuestsPage", canvasObject.transform).GetComponent<RectTransform>();
            var controller = controllerObject.AddComponent<QuestClientController>();
            using (new TemporaryAdminQuestTab(page, controller, (_, _) => { }))
            {
                Assert.That(Texts(page), Has.Some.Contains("once they are loaded"));

                controller.State.SetCatalog(FixtureQuestCatalog.Load());
                controller.State.PrepareCharacter(CharacterId, 1);
                Assert.That(controller.State.TryApplyLog(Log(), out var error), Is.True, error);

                var texts = Texts(page);
                Assert.That(texts, Does.Contain("Fixture Wolves"));
                Assert.That(texts, Does.Contain("active, step 1 of 1"));
                Assert.That(texts, Does.Contain("needs an earlier quest"));
                Assert.That(texts, Does.Contain("completed 2 times, available again"));
                Assert.That(texts, Does.Contain("available"));
                var buttons = Buttons(page);
                Assert.That(buttons, Does.Contain("Complete step"));
                Assert.That(buttons, Does.Contain("Make ready"));
                Assert.That(buttons, Does.Contain("Complete: Feral Wolves slain"));
                Assert.That(buttons.Count(label => label == "Accept"), Is.EqualTo(3), "Every quest but the active one.");
                Assert.That(buttons.Count(label => label == "Reset"), Is.EqualTo(2), "The active wolves and the completed ore run.");
                Assert.That(buttons, Does.Contain("Reset all quests"));
            }
        }

        private static CharacterQuestLogResponse Log()
        {
            return new CharacterQuestLogResponse
            {
                characterId = CharacterId.ToString(),
                questCatalogRevision = FixtureQuestCatalog.Load().Revision,
                questLogRevision = 3,
                activeQuests = new[]
                {
                    new ActiveQuestResponse
                    {
                        questId = FixtureQuestCatalog.Wolves,
                        stepId = "hunt",
                        objectives = new[] { new QuestObjectiveProgressResponse { objectiveId = "wolves", progress = 1 } }
                    }
                },
                completedQuests = new[]
                {
                    new CompletedQuestResponse { questId = FixtureQuestCatalog.OreRun, completionCount = 2 }
                }
            };
        }

        private static List<string> Texts(RectTransform page)
        {
            return page.GetComponentsInChildren<TextMeshProUGUI>(true).Select(text => text.text).ToList();
        }

        private static List<string> Buttons(RectTransform page)
        {
            return page.GetComponentsInChildren<Button>(true)
                .Select(button => button.GetComponentInChildren<TextMeshProUGUI>(true).text)
                .ToList();
        }
    }
}
