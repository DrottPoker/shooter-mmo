using System;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Quests;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The tracker at the right edge of the screen shows each active quest with the current
    // step's objectives, or that it is ready to turn in, and hides without active quests.
    public sealed class QuestTrackerTests
    {
        private GameObject canvasObject;
        private UiTheme theme;

        [SetUp]
        public void SetUp()
        {
            canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            theme = ScriptableObject.CreateInstance<UiTheme>();
            theme.TitleFont = TMP_Settings.defaultFontAsset;
            theme.BodyFont = TMP_Settings.defaultFontAsset;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(canvasObject);
            UnityEngine.Object.DestroyImmediate(theme);
        }

        [Test]
        public void TheTrackerShowsEachActiveQuestsObjectivesOrThatItIsReady()
        {
            var tracker = new QuestTracker(theme, FixtureTexts.Ui(), canvasObject.GetComponent<RectTransform>());
            var state = new QuestClientState();
            state.SetCatalog(FixtureQuestCatalog.Load());
            var characterId = Guid.NewGuid();
            state.PrepareCharacter(characterId, 1);

            tracker.Refresh(state, _ => 0);
            Assert.That(tracker.Panel.gameObject.activeSelf, Is.False);

            Assert.That(state.TryApplyLog(new CharacterQuestLogResponse
            {
                characterId = characterId.ToString(),
                questCatalogRevision = state.Catalog.Revision,
                questLogRevision = 2,
                activeQuests = new[]
                {
                    new ActiveQuestResponse
                    {
                        questId = FixtureQuestCatalog.Wolves,
                        stepId = "hunt",
                        objectives = new[] { new QuestObjectiveProgressResponse { objectiveId = "wolves", progress = 2 } }
                    },
                    new ActiveQuestResponse { questId = FixtureQuestCatalog.Trophies, stepId = "collect", objectives = Array.Empty<QuestObjectiveProgressResponse>() }
                },
                completedQuests = Array.Empty<CompletedQuestResponse>()
            }, out var error), Is.True, error);
            tracker.Refresh(state, id => id == "fixture.trophy" ? 1 : 0);

            Assert.That(tracker.Panel.gameObject.activeSelf, Is.True);
            var lines = tracker.Text.Split('\n').Select(Plain).ToArray();
            Assert.That(lines, Is.EqualTo(new[]
            {
                "Fixture Trophies",
                "  Stolen trophies 1/2",
                "Fixture Wolves",
                "  Ready to turn in"
            }));
        }

        // The tracker's text without its rich text tags.
        private static string Plain(string line)
        {
            return System.Text.RegularExpressions.Regex.Replace(line, "<[^>]*>", string.Empty);
        }
    }
}
