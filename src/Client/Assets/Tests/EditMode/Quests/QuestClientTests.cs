using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Api;
using Mmorpg.Client.Quests;
using Mmorpg.GameProtocol;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The client's quest log against the fixture catalog: a load, the realtime changes in
    // revision order, the quest dialog's entries at Mira, and the notices of a change.
    public sealed class QuestClientTests
    {
        private static readonly Guid CharacterId = Guid.Parse("c0000000-0000-4000-8000-000000000001");

        [Test]
        public void ALoadHoldsTheCharactersLogForTheBundledCatalog()
        {
            var state = Ready(Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 1)));

            Assert.That(state.IsReady, Is.True);
            Assert.That(state.Revision, Is.EqualTo(3));
            Assert.That(state.ActiveQuests[FixtureQuestCatalog.Wolves].ProgressOf("wolves"), Is.EqualTo(1));
            Assert.That(state.CompletionCountOf(FixtureQuestCatalog.OreRun), Is.EqualTo(2));

            var other = Prepared();
            var foreign = Log(3);
            foreign.characterId = Guid.NewGuid().ToString();
            Assert.That(other.TryApplyLog(foreign, out _), Is.False);
            var outdated = Log(3);
            outdated.questCatalogRevision = new string('0', 64);
            Assert.That(other.TryApplyLog(outdated, out _), Is.False);
            Assert.That(other.Status, Is.EqualTo(QuestClientStatus.UpdateRequired));
        }

        [Test]
        public void ChangesApplyInRevisionOrderAndAGapAsksForAReload()
        {
            var state = Ready();
            IReadOnlyList<QuestClientChange> touched = null;
            state.QuestsChanged += changes => touched = changes;

            var accepted = state.ApplyChange(Change(3, 4, Quest(FixtureQuestCatalog.Wolves, "hunt", 0, ("wolves", 0))));
            var replayed = state.ApplyChange(Change(3, 4, Quest(FixtureQuestCatalog.Wolves, "hunt", 0, ("wolves", 0))));
            var skipped = state.ApplyChange(Change(5, 6, Quest(FixtureQuestCatalog.Wolves, "hunt", 0, ("wolves", 2))));
            var reload = state.ApplyChange(new RealtimeQuestLogChanged(Guid.Empty, 4, 5, true, Array.Empty<RealtimeQuestChange>()));

            Assert.That(
                new[] { accepted, replayed, skipped, reload },
                Is.EqualTo(new[]
                {
                    QuestLogApplyResult.Applied,
                    QuestLogApplyResult.Stale,
                    QuestLogApplyResult.ReloadRequired,
                    QuestLogApplyResult.ReloadRequired
                }));
            Assert.That(state.Revision, Is.EqualTo(4));
            var change = touched.Single();
            Assert.That(change.Before, Is.Null);
            Assert.That(change.After.StepId, Is.EqualTo("hunt"));

            state.ApplyChange(Change(4, 5, Quest(FixtureQuestCatalog.Wolves, string.Empty, 1)));
            Assert.That(state.IsActive(FixtureQuestCatalog.Wolves), Is.False);
            Assert.That(state.CompletionCountOf(FixtureQuestCatalog.Wolves), Is.EqualTo(1));
        }

        [Test]
        public void MiraOffersWhatTheCharacterMayAcceptAndCompletesWhatIsReady()
        {
            var fresh = Entries(Ready(), _ => 0);
            Assert.That(
                fresh.Select(entry => entry.Kind + " " + entry.Quest.Id),
                Is.EquivalentTo(new[]
                {
                    "Offer " + FixtureQuestCatalog.Wolves,
                    "Offer " + FixtureQuestCatalog.OreRun,
                    "Offer " + FixtureQuestCatalog.Trophies
                }));

            var hunting = Entries(Ready(Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 1))), _ => 0);
            Assert.That(
                hunting.Single(entry => entry.Quest.Id == FixtureQuestCatalog.Wolves).Kind,
                Is.EqualTo(QuestDialogEntryKind.InProgress));

            var hunted = Entries(Ready(Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 2))), _ => 0);
            Assert.That(hunted.First().Kind, Is.EqualTo(QuestDialogEntryKind.TurnIn));
            Assert.That(hunted.First().Quest.Id, Is.EqualTo(FixtureQuestCatalog.Wolves));

            // Two carried letters make the trophies ready; one keeps them in progress.
            var trophies = Ready(Active(FixtureQuestCatalog.Trophies, "collect"));
            Assert.That(Entries(trophies, id => id == "fixture.letter" ? 2 : 0).First().Kind, Is.EqualTo(QuestDialogEntryKind.TurnIn));
            Assert.That(Entries(trophies, id => id == "fixture.letter" ? 1 : 0).Last().Kind, Is.EqualTo(QuestDialogEntryKind.InProgress));
        }

        [Test]
        public void TheErrandShowsItsTalkDeliveryAndLostLetterAtMira()
        {
            var state = Ready(Active(FixtureQuestCatalog.Errand, "work", ("ore", 2), ("hand_over", 0), ("report", 0)));

            var entries = Entries(state, _ => 0);

            Assert.That(
                entries.Where(entry => entry.Quest.Id == FixtureQuestCatalog.Errand).Select(entry => entry.Kind + " " + entry.Objective?.Id),
                Is.EqualTo(new[]
                {
                    "Deliver hand_over",
                    "TalkTo report",
                    "LostStartItems ",
                    "InProgress "
                }));
            var carrying = Entries(state, id => id == "fixture.letter" ? 1 : 0);
            Assert.That(carrying.Any(entry => entry.Kind == QuestDialogEntryKind.LostStartItems), Is.False);
        }

        [Test]
        public void AnErrandDeliveryWaitsForTheWholeCount()
        {
            var state = Ready(Active(FixtureQuestCatalog.Errand, "work", ("ore", 2), ("hand_over", 0), ("report", 0)));
            var delivery = Entries(state, _ => 0).Single(entry => entry.Kind == QuestDialogEntryKind.Deliver);

            Assert.That(QuestDialogEntries.CanHandOver(delivery, _ => 0), Is.False);
            Assert.That(QuestDialogEntries.CanHandOver(delivery, id => id == "fixture.ore" ? 1 : 0), Is.True);
            Assert.That(QuestDialogEntries.CanHandOver(Entries(state, _ => 0).First(entry => entry.Kind == QuestDialogEntryKind.TalkTo), _ => 1), Is.False);
        }

        // Mira's marker shows the most urgent of what waits at her: a quest to turn in over an
        // offer, and an offer over a quest in progress.
        [Test]
        public void MiraShowsTheMostUrgentOfWhatWaitsThere()
        {
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, Ready(), _ => 0), Is.EqualTo(QuestMarker.Available));
            Assert.That(
                QuestMarkers.For(FixtureQuestCatalog.Mira, Ready(Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 2))), _ => 0),
                Is.EqualTo(QuestMarker.Ready));

            var busy = Ready(
                Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 1)),
                Active(FixtureQuestCatalog.OreRun, "deliver"),
                Active(FixtureQuestCatalog.Trophies, "collect"));
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, busy, _ => 0), Is.EqualTo(QuestMarker.InProgress));
            Assert.That(
                QuestMarkers.For(FixtureQuestCatalog.Mira, busy, id => id == "fixture.letter" ? 2 : 0),
                Is.EqualTo(QuestMarker.Ready),
                "Two carried letters make the trophies ready to turn in.");
            Assert.That(QuestMarkers.For("npc.outpost_sergeant", busy, _ => 0), Is.EqualTo(QuestMarker.None));
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, Prepared(), _ => 0), Is.EqualTo(QuestMarker.None), "No marker shows before the log loads.");

            // The errand's report and lost letter wait at Mira as well as its turn-in.
            var errand = Ready(Active(FixtureQuestCatalog.Errand, "work", ("ore", 2), ("hand_over", 0), ("report", 0)));
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, errand, _ => 0), Is.EqualTo(QuestMarker.Ready));
        }

        [Test]
        public void AQuestThatOnlyNeedsAHigherLevelShowsAGreyExclamationMark()
        {
            var catalog = FixtureQuestCatalog.Load();
            foreach (var quest in catalog.Quests)
            {
                quest.MinimumLevel = 5;
            }

            var state = new QuestClientState();
            state.SetCatalog(catalog);
            state.PrepareCharacter(CharacterId, 4);
            Assert.That(state.TryApplyLog(Log(3), out var error), Is.True, error);
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, state, _ => 0), Is.EqualTo(QuestMarker.HigherLevelQuest));

            state.PrepareCharacter(CharacterId, 5);
            Assert.That(state.TryApplyLog(Log(3), out error), Is.True, error);
            Assert.That(QuestMarkers.For(FixtureQuestCatalog.Mira, state, _ => 0), Is.EqualTo(QuestMarker.Available));
        }

        [Test]
        public void AQuestDropInACorpseNamesTheMobTheItemAndTheQuest()
        {
            var catalog = FixtureQuestCatalog.Load();
            Func<string, string> names = id => id == "fixture.letter" ? "Stolen Letter" : null;

            Assert.That(
                QuestNotices.ForDrop(new RealtimeQuestDropInCorpse(Guid.NewGuid(), "Feral Wolf", FixtureQuestCatalog.Trophies, "fixture.letter", 1), catalog, names),
                Is.EqualTo("Feral Wolf dropped Stolen Letter for Fixture Trophies."));
            Assert.That(
                QuestNotices.ForDrop(new RealtimeQuestDropInCorpse(Guid.NewGuid(), "Feral Alpha", "quest.unknown", "fixture.fang", 2), catalog, names),
                Is.EqualTo("Feral Alpha dropped 2 x fixture.fang for quest.unknown."),
                "An unknown item or quest shows its id.");
        }

        [Test]
        public void ACommittedChangeTellsTheProgressAndTheOutcome()
        {
            var catalog = FixtureQuestCatalog.Load();
            var hunting = new QuestClientActiveQuest(FixtureQuestCatalog.Wolves, "hunt", Progress(("wolves", 0)));
            var oneKill = new QuestClientActiveQuest(FixtureQuestCatalog.Wolves, "hunt", Progress(("wolves", 1)));
            var twoKills = new QuestClientActiveQuest(FixtureQuestCatalog.Wolves, "hunt", Progress(("wolves", 2)));

            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Wolves, null, hunting, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Quest accepted: Fixture Wolves" }));
            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Wolves, hunting, oneKill, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Feral Wolves slain 1/2" }));
            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Wolves, oneKill, twoKills, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Feral Wolves slain 2/2" }));
            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Wolves, twoKills, null, 0, 1) }, catalog),
                Is.EqualTo(new[] { "Quest completed: Fixture Wolves" }));
            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Wolves, oneKill, null, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Quest abandoned: Fixture Wolves" }));
        }

        // A step moves on once it is done, so the counts that the change finished show in full
        // before the update, also when a tool finished the whole step at once.
        [Test]
        public void AStepThatMovesOnShowsTheCountsItFinishedFirst()
        {
            var catalog = FixtureQuestCatalog.Load();
            var reporting = new QuestClientActiveQuest(FixtureQuestCatalog.Errand, "work", Progress(("ore", 2), ("hand_over", 1), ("report", 0)));
            var started = new QuestClientActiveQuest(FixtureQuestCatalog.Errand, "work", Progress(("ore", 1), ("hand_over", 0), ("report", 0)));
            var returning = new QuestClientActiveQuest(FixtureQuestCatalog.Errand, "return", Progress());

            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Errand, reporting, returning, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Report the mining 1/1", "Quest updated: Fixture Errand" }));
            Assert.That(
                QuestNotices.For(new[] { new QuestClientChange(FixtureQuestCatalog.Errand, started, returning, 0, 0) }, catalog),
                Is.EqualTo(new[] { "Ore mined 2/2", "Ore handed over 1/1", "Report the mining 1/1", "Quest updated: Fixture Errand" }));
        }

        // An offer counts nothing yet, but a collect objective counts the carried items, as it
        // will right after the quest is accepted.
        [Test]
        public void AnOfferCountsTheCarriedItemsOfItsCollectObjectives()
        {
            var catalog = FixtureQuestCatalog.Load();
            Func<string, int> carried = id => id == "fixture.ore" ? 1 : 0;
            Assert.That(catalog.TryGet(FixtureQuestCatalog.OreRun, out var oreRun), Is.True);
            Assert.That(catalog.TryGet(FixtureQuestCatalog.Errand, out var errand), Is.True);

            Assert.That(QuestProgress.OfferedObjectives(oreRun, carried).Select(line => line.Text), Is.EqualTo(new[] { "Ore 1/3" }));
            Assert.That(
                QuestProgress.OfferedObjectives(errand, carried).Select(line => line.Text),
                Is.EqualTo(new[] { "Ore mined 0/2", "Ore handed over 0/1", "Report the mining 0/1" }));
        }

        // Carried letters move the trophies' collect objective, and the quest says once that it is
        // ready, whether a kill or an item made it so. A load tells nothing.
        [Test]
        public void TheWatcherTellsCollectProgressAndReadinessOnce()
        {
            var letters = 0;
            Func<string, int> carried = id => id == "fixture.letter" ? letters : 0;
            var state = Ready(
                Active(FixtureQuestCatalog.Trophies, "collect"),
                Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 1)));
            var watcher = new QuestProgressWatcher();

            Assert.That(watcher.Update(state, carried), Is.Empty);
            letters = 1;
            Assert.That(watcher.Update(state, carried), Is.EqualTo(new[] { "Stolen letters 1/2" }));
            letters = 2;
            Assert.That(
                watcher.Update(state, carried),
                Is.EqualTo(new[] { "Stolen letters 2/2", "Fixture Trophies is ready to turn in." }));
            letters = 3;
            Assert.That(watcher.Update(state, carried), Is.Empty);

            state.ApplyChange(Change(3, 4, Quest(FixtureQuestCatalog.Wolves, "hunt", 0, ("wolves", 2))));
            Assert.That(watcher.Update(state, carried), Is.EqualTo(new[] { "Fixture Wolves is ready to turn in." }));
            state.ApplyChange(Change(4, 5, Quest(FixtureQuestCatalog.OreRun, "deliver", 2)));
            Assert.That(watcher.Update(state, carried), Is.Empty);

            // A quest that is ready when it is accepted says so at once.
            var fresh = Ready();
            var freshWatcher = new QuestProgressWatcher();
            freshWatcher.Update(fresh, carried);
            fresh.ApplyChange(Change(3, 4, Quest(FixtureQuestCatalog.Trophies, "collect", 0)));
            Assert.That(freshWatcher.Update(fresh, carried), Is.EqualTo(new[] { "Fixture Trophies is ready to turn in." }));
        }

        // A failed read of the quest log is tried again after 2 s, then 4 s, up to 30 s, and a
        // read that succeeds starts over.
        [Test]
        public void AFailedQuestLogReadIsTriedAgainWithAGrowingDelay()
        {
            var retry = new QuestLogRetry();
            Assert.That(retry.IsDue(1000f), Is.False);

            retry.Failed(10f);
            Assert.That(new[] { retry.IsDue(11.9f), retry.IsDue(12f) }, Is.EqualTo(new[] { false, true }));
            retry.Failed(12f);
            Assert.That(new[] { retry.IsDue(15.9f), retry.IsDue(16f) }, Is.EqualTo(new[] { false, true }));
            for (var attempt = 0; attempt < 5; attempt++)
            {
                retry.Failed(100f);
            }

            Assert.That(new[] { retry.IsDue(129.9f), retry.IsDue(130f) }, Is.EqualTo(new[] { false, true }));
            retry.Reset();
            Assert.That(retry.IsDue(1000f), Is.False);
            retry.Failed(200f);
            Assert.That(retry.IsDue(202f), Is.True);
        }

        // The quest log and the tracker list the active quests by title with the current step's
        // objectives, a ready quest as ready, and the completed quests with their counts.
        [Test]
        public void TheQuestLogListsActiveQuestsWithTheirObjectivesAndTheCompletedOnes()
        {
            var state = Ready(
                Active(FixtureQuestCatalog.Wolves, "hunt", ("wolves", 2)),
                Active(FixtureQuestCatalog.Trophies, "collect"));

            var active = QuestLogModel.Active(state, id => id == "fixture.letter" ? 1 : 0);
            var completed = QuestLogModel.Completed(state);

            Assert.That(active.Select(entry => entry.Quest.Title), Is.EqualTo(new[] { "Fixture Trophies", "Fixture Wolves" }));
            Assert.That(active[0].Objectives.Select(line => line.Text), Is.EqualTo(new[] { "Stolen letters 1/2" }));
            Assert.That(active.Select(entry => entry.IsReady), Is.EqualTo(new[] { false, true }));
            Assert.That(completed.Single().Quest.Id, Is.EqualTo(FixtureQuestCatalog.OreRun));
            Assert.That(completed.Single().CompletionCount, Is.EqualTo(2));
        }

        private static IReadOnlyList<QuestDialogEntry> Entries(QuestClientState state, Func<string, int> carried)
        {
            return QuestDialogEntries.For(FixtureQuestCatalog.Mira, state, carried);
        }

        private static QuestClientState Prepared()
        {
            var state = new QuestClientState();
            state.SetCatalog(FixtureQuestCatalog.Load());
            state.PrepareCharacter(CharacterId, 1);
            return state;
        }

        private static QuestClientState Ready(params ActiveQuestResponse[] active)
        {
            var state = Prepared();
            Assert.That(state.TryApplyLog(Log(3, active), out var error), Is.True, error);
            return state;
        }

        private static CharacterQuestLogResponse Log(long revision, params ActiveQuestResponse[] active)
        {
            return new CharacterQuestLogResponse
            {
                characterId = CharacterId.ToString(),
                questCatalogRevision = FixtureQuestCatalog.Load().Revision,
                questLogRevision = revision,
                activeQuests = active,
                completedQuests = new[]
                {
                    new CompletedQuestResponse { questId = FixtureQuestCatalog.OreRun, completionCount = 2 }
                }
            };
        }

        private static ActiveQuestResponse Active(string questId, string stepId, params (string Id, int Progress)[] objectives)
        {
            return new ActiveQuestResponse
            {
                questId = questId,
                stepId = stepId,
                objectives = objectives
                    .Select(objective => new QuestObjectiveProgressResponse { objectiveId = objective.Id, progress = objective.Progress })
                    .ToArray()
            };
        }

        private static RealtimeQuestLogChanged Change(long previous, long revision, params RealtimeQuestChange[] quests)
        {
            return new RealtimeQuestLogChanged(Guid.Empty, previous, revision, false, quests);
        }

        private static RealtimeQuestChange Quest(string questId, string stepId, int completions, params (string Id, int Progress)[] objectives)
        {
            return new RealtimeQuestChange(
                questId,
                stepId,
                objectives.Select(objective => new RealtimeQuestObjectiveProgress(objective.Id, objective.Progress)).ToArray(),
                completions);
        }

        private static IReadOnlyDictionary<string, int> Progress(params (string Id, int Progress)[] objectives)
        {
            return objectives.ToDictionary(objective => objective.Id, objective => objective.Progress, StringComparer.Ordinal);
        }
    }
}
