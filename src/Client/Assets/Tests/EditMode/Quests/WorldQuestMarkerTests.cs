using Mmorpg.Client.Quests;
using Mmorpg.Client.Ui;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The glyph, color, and size of the quest marker over an NPC.
    public sealed class WorldQuestMarkerTests
    {
        [Test]
        public void EveryMarkerHasItsGlyphAndColor()
        {
            Assert.That(WorldQuestMarkers.TextOf(QuestMarker.None), Is.Empty);
            Assert.That(WorldQuestMarkers.TextOf(QuestMarker.HigherLevelQuest), Is.EqualTo("!"));
            Assert.That(WorldQuestMarkers.ColorOf(QuestMarker.HigherLevelQuest), Is.EqualTo(WorldQuestMarkers.WaitingColor));
            Assert.That(WorldQuestMarkers.TextOf(QuestMarker.InProgress), Is.EqualTo("?"));
            Assert.That(WorldQuestMarkers.ColorOf(QuestMarker.InProgress), Is.EqualTo(WorldQuestMarkers.WaitingColor));
            Assert.That(WorldQuestMarkers.TextOf(QuestMarker.Available), Is.EqualTo("!"));
            Assert.That(WorldQuestMarkers.ColorOf(QuestMarker.Available), Is.EqualTo(WorldQuestMarkers.UrgentColor));
            Assert.That(WorldQuestMarkers.TextOf(QuestMarker.Ready), Is.EqualTo("?"));
            Assert.That(WorldQuestMarkers.ColorOf(QuestMarker.Ready), Is.EqualTo(WorldQuestMarkers.UrgentColor));
        }

        [Test]
        public void AFartherMarkerIsSmallerLikeANameplate()
        {
            Assert.That(WorldQuestMarkers.ScaleAt(0f), Is.EqualTo(1f));
            Assert.That(WorldQuestMarkers.ScaleAt(NameplatePresentation.FullSizeDistanceMetres), Is.EqualTo(1f));
            Assert.That(WorldQuestMarkers.ScaleAt(WorldQuestMarkers.RangeMetres), Is.EqualTo(NameplatePresentation.FarScale));
            Assert.That(
                WorldQuestMarkers.ScaleAt(25f),
                Is.InRange(NameplatePresentation.FarScale, 1f));
        }
    }
}
