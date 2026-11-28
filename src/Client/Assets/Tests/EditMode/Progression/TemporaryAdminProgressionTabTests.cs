using System;
using System.Collections.Generic;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The Admin menu's Progression tab reads out the worker's progression, checks a level and an
    // amount of experience against the protocol's bounds before it sends, and shows the answer to
    // its own last intent.
    public sealed class TemporaryAdminProgressionTabTests
    {
        private GameObject canvasObject;
        private RectTransform page;
        private ProgressionClientState progression;
        private List<(RealtimeAdminIntent Intent, string Description)> sent;
        private bool sendSucceeds;

        [SetUp]
        public void SetUp()
        {
            canvasObject = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            page = TemporaryUiKit.CreateUiObject("ProgressionPage", canvasObject.transform).GetComponent<RectTransform>();
            progression = new ProgressionClientState();
            sent = new List<(RealtimeAdminIntent, string)>();
            sendSucceeds = true;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(canvasObject);
        }

        [Test]
        public void TheReadoutFollowsTheProgression()
        {
            using (var tab = CreateTab())
            {
                Assert.That(tab.ReadoutText, Does.Contain("joined a World"));

                progression.Apply(new RealtimeOwnerProgressionChanged(12, 340, 910, 9, 0, 0, RealtimeProgressionChangeReason.Snapshot));
                Assert.That(tab.ReadoutText, Is.EqualTo("Level 12, 340 / 910 XP"));

                progression.Apply(new RealtimeOwnerProgressionChanged(200, 0, 0, int.MaxValue, 0, 1, RealtimeProgressionChangeReason.Admin));
                Assert.That(tab.ReadoutText, Is.EqualTo("Level 200, highest level"));
            }
        }

        [Test]
        public void TheInputIsCheckedAgainstTheProtocolBounds()
        {
            Assert.That(TemporaryAdminProgressionTab.TryParseLevel("1", out var lowest, out _), Is.True);
            Assert.That(lowest, Is.EqualTo(1));
            Assert.That(TemporaryAdminProgressionTab.TryParseLevel(" 10000 ", out var highest, out _), Is.True);
            Assert.That(highest, Is.EqualTo(RealtimeProtocolLimits.MaximumAdminCharacterLevel));
            foreach (var invalid in new[] { "0", "-3", "10001", "70000", "", "twelve", "1.5", null })
            {
                Assert.That(TemporaryAdminProgressionTab.TryParseLevel(invalid, out _, out var error), Is.False, invalid);
                Assert.That(error, Is.EqualTo("Enter a level from 1 to 10,000."));
            }

            Assert.That(TemporaryAdminProgressionTab.TryParseExperience("1000000000", out var most, out _), Is.True);
            Assert.That(most, Is.EqualTo(TemporaryAdminProgressionTab.MaximumGrantedExperience));
            foreach (var invalid in new[] { "0", "-1", "1000000001", "99999999999999999999", "" })
            {
                Assert.That(TemporaryAdminProgressionTab.TryParseExperience(invalid, out _, out var error), Is.False, invalid);
                Assert.That(error, Is.EqualTo("Enter an amount of experience from 1 to 1,000,000,000."));
            }

            // The bounds are the protocol's: the encoder takes the edges and refuses beyond them.
            Assert.DoesNotThrow(() => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetLevel(Guid.NewGuid(), highest)));
            Assert.DoesNotThrow(() => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateGrantExperience(Guid.NewGuid(), most)));
            Assert.Throws<ArgumentException>(() => RealtimeProtocol.EncodeAdminIntent(
                RealtimeAdminIntent.CreateGrantExperience(Guid.NewGuid(), TemporaryAdminProgressionTab.MaximumGrantedExperience + 1)));
        }

        [Test]
        public void ValidInputSendsTheIntentAndInvalidInputSendsNothing()
        {
            using (var tab = CreateTab())
            {
                Assert.That(tab.RequestSetLevel("abc"), Is.False);
                Assert.That(tab.RequestGrantExperience("0"), Is.False);
                Assert.That(sent, Is.Empty);
                Assert.That(tab.ResultText, Is.EqualTo("Enter an amount of experience from 1 to 1,000,000,000."));

                Assert.That(tab.RequestSetLevel("60"), Is.True);
                Assert.That(tab.RequestGrantExperience("1500"), Is.True);

                Assert.That(sent.Count, Is.EqualTo(2));
                Assert.That((sent[0].Intent.Kind, sent[0].Intent.Level), Is.EqualTo((RealtimeAdminIntentKind.SetLevel, (ushort)60)));
                Assert.That((sent[1].Intent.Kind, sent[1].Intent.Experience), Is.EqualTo((RealtimeAdminIntentKind.GrantExperience, 1500L)));
                Assert.That(sent[1].Description, Is.EqualTo("Grant 1,500 experience"));
                Assert.That(tab.ResultText, Is.EqualTo("Grant 1,500 experience..."));
            }
        }

        // The tab shows the answer to its own intents: done, or the worker's error.
        [Test]
        public void TheAnswerToTheLastIntentShows()
        {
            using (var tab = CreateTab())
            {
                Assert.That(tab.RequestSetLevel("60"), Is.True);
                var setLevel = sent[0].Intent;

                tab.OnIntentCompleted(new RealtimeAdminIntentResult(Guid.NewGuid(), RealtimeAdminIntentKind.SetLevel, false, null));
                Assert.That(tab.ResultText, Is.EqualTo("Set level 60..."), "Another intent's answer is not the tab's.");

                tab.OnIntentCompleted(new RealtimeAdminIntentResult(setLevel.OperationId, RealtimeAdminIntentKind.SetLevel, true, null));
                Assert.That(tab.ResultText, Is.EqualTo("Done."));

                Assert.That(tab.RequestGrantExperience("25"), Is.True);
                tab.OnIntentCompleted(new RealtimeAdminIntentResult(
                    sent[1].Intent.OperationId,
                    RealtimeAdminIntentKind.GrantExperience,
                    false,
                    new RealtimeError("admin_tools_unavailable", "GameService refused the development tool.")));
                Assert.That(tab.ResultText, Is.EqualTo("GameService refused the development tool."));

                sendSucceeds = false;
                Assert.That(tab.RequestSetLevel("3"), Is.False);
                Assert.That(tab.ResultText, Is.EqualTo("Set level 3 could not be sent."));
            }
        }

        // A session that ends before the answer leaves no line that waits forever, and a late
        // answer to the forgotten intent shows nothing.
        [Test]
        public void ASessionEndBeforeTheAnswerSaysThatNoAnswerCame()
        {
            using (var tab = CreateTab())
            {
                tab.ForgetPending();
                Assert.That(tab.ResultText, Is.Empty, "Nothing waited, so nothing changes.");

                Assert.That(tab.RequestSetLevel("60"), Is.True);
                Assert.That(tab.ResultText, Is.EqualTo("Set level 60..."));

                tab.ForgetPending();
                Assert.That(tab.ResultText, Is.EqualTo(TemporaryAdminProgressionTab.NoAnswerText));

                tab.OnIntentCompleted(new RealtimeAdminIntentResult(sent[0].Intent.OperationId, RealtimeAdminIntentKind.SetLevel, true, null));
                Assert.That(tab.ResultText, Is.EqualTo(TemporaryAdminProgressionTab.NoAnswerText));
            }
        }

        private TemporaryAdminProgressionTab CreateTab()
        {
            return new TemporaryAdminProgressionTab(page, progression, (intent, description) =>
            {
                if (!sendSucceeds)
                {
                    return false;
                }

                sent.Add((intent, description));
                return true;
            });
        }
    }
}
