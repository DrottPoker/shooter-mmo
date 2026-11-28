using System;
using System.Collections.Generic;
using System.Globalization;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using TMPro;
using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // The Progression tab of the Admin menu: the character's level and experience as the worker
    // sent them, a level to set, and experience to grant. A Development worker carries them out
    // through GameService; the changed progression arrives before the answer, so the readout
    // follows the progression state and the tab shows only the answer to its own last intent.
    public sealed class TemporaryAdminProgressionTab : IDisposable
    {
        // The most experience one grant may carry, as the protocol bounds it.
        public const long MaximumGrantedExperience = 1000000000;

        private readonly ProgressionClientState progression;
        private readonly Func<RealtimeAdminIntent, string, bool> send;
        private readonly HashSet<Guid> pendingIntents = new HashSet<Guid>();
        private readonly TextMeshProUGUI readout;
        private readonly TMP_InputField levelField;
        private readonly TMP_InputField experienceField;
        private readonly TextMeshProUGUI result;

        // The send function is the menu's, which reports to its footer too and returns whether
        // the intent went out.
        public TemporaryAdminProgressionTab(
            RectTransform page,
            ProgressionClientState progression,
            Func<RealtimeAdminIntent, string, bool> send)
        {
            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }

            this.progression = progression;
            this.send = send ?? throw new ArgumentNullException(nameof(send));

            TemporaryMenuWindow.AddSection(page, "Progression");
            readout = TemporaryMenuWindow.AddValue(page, "Now");

            TemporaryMenuWindow.AddSection(page, "Level");
            var levelRow = TemporaryMenuWindow.AddButtonRow(page);
            levelField = TemporaryUiKit.CreateIntegerField(levelRow, string.Empty, 120f);
            TemporaryUiKit.CreateButton(levelRow, "Set level", () => RequestSetLevel(levelField.text), true, TemporaryButtonTone.Normal);
            TemporaryMenuWindow.AddNote(page, "The level starts with no experience inside it. A lower level shows no level-up.");

            TemporaryMenuWindow.AddSection(page, "Experience");
            var experienceRow = TemporaryMenuWindow.AddButtonRow(page);
            experienceField = TemporaryUiKit.CreateIntegerField(experienceRow, string.Empty, 120f);
            TemporaryUiKit.CreateButton(
                experienceRow,
                "Grant experience",
                () => RequestGrantExperience(experienceField.text),
                true,
                TemporaryButtonTone.Normal);
            TemporaryMenuWindow.AddNote(page, "Levels up as far as the experience reaches.");

            result = TemporaryMenuWindow.AddNote(page, string.Empty);
            result.fontStyle = FontStyles.Normal;
            result.gameObject.SetActive(false);

            if (progression != null)
            {
                progression.Changed += OnProgressionChanged;
            }

            Refresh();
        }

        public string ReadoutText
        {
            get { return readout.text; }
        }

        // The answer to the tab's last intent, or why it was not sent; empty before one.
        public string ResultText
        {
            get { return result.gameObject.activeSelf ? result.text : string.Empty; }
        }

        public void Dispose()
        {
            if (progression != null)
            {
                progression.Changed -= OnProgressionChanged;
            }
        }

        // Such as "Level 12, 340 / 910 XP" or "Level 200, highest level".
        public static string ReadoutFor(RealtimeOwnerProgressionChanged current)
        {
            if (current == null)
            {
                return "Shows once you joined a World.";
            }

            var level = "Level " + Number(current.Level);
            return current.ExperienceToNextLevel <= 0
                ? level + ", highest level"
                : level + ", " + Number(current.Experience) + " / " + Number(current.ExperienceToNextLevel) + " XP";
        }

        // A whole level from 1 to the protocol's highest admin level.
        public static bool TryParseLevel(string text, out ushort level, out string error)
        {
            level = 0;
            error = "Enter a level from 1 to " + Number(RealtimeProtocolLimits.MaximumAdminCharacterLevel) + ".";
            if (!long.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < 1
                || value > RealtimeProtocolLimits.MaximumAdminCharacterLevel)
            {
                return false;
            }

            level = (ushort)value;
            error = string.Empty;
            return true;
        }

        // A whole amount of experience from 1 to the protocol's largest grant.
        public static bool TryParseExperience(string text, out long experience, out string error)
        {
            experience = 0;
            error = "Enter an amount of experience from 1 to " + Number(MaximumGrantedExperience) + ".";
            if (!long.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < 1
                || value > MaximumGrantedExperience)
            {
                return false;
            }

            experience = value;
            error = string.Empty;
            return true;
        }

        public bool RequestSetLevel(string text)
        {
            if (!TryParseLevel(text, out var level, out var error))
            {
                ShowResult(error, TemporaryUiKit.WarningColor);
                return false;
            }

            return Send(RealtimeAdminIntent.CreateSetLevel(Guid.NewGuid(), level), "Set level " + Number(level));
        }

        public bool RequestGrantExperience(string text)
        {
            if (!TryParseExperience(text, out var experience, out var error))
            {
                ShowResult(error, TemporaryUiKit.WarningColor);
                return false;
            }

            return Send(
                RealtimeAdminIntent.CreateGrantExperience(Guid.NewGuid(), experience),
                "Grant " + Number(experience) + " experience");
        }

        // The menu hands on every answer; the tab shows those to its own intents.
        public void OnIntentCompleted(RealtimeAdminIntentResult answer)
        {
            if (answer == null || !pendingIntents.Remove(answer.OperationId))
            {
                return;
            }

            if (answer.Succeeded)
            {
                ShowResult("Done.", TemporaryUiKit.SuccessColor);
            }
            else
            {
                ShowResult(answer.Error != null ? answer.Error.Message : "Refused.", TemporaryUiKit.DangerColor);
            }
        }

        // The tools are off or the session ended: no answer comes for what was sent.
        public void ForgetPending()
        {
            pendingIntents.Clear();
        }

        private bool Send(RealtimeAdminIntent intent, string description)
        {
            if (!send(intent, description))
            {
                ShowResult(description + " could not be sent.", TemporaryUiKit.DangerColor);
                return false;
            }

            pendingIntents.Add(intent.OperationId);
            ShowResult(description + "...", TemporaryUiKit.TextSecondary);
            return true;
        }

        private void ShowResult(string text, Color color)
        {
            result.text = text;
            result.color = color;
            result.gameObject.SetActive(true);
        }

        private void OnProgressionChanged(RealtimeOwnerProgressionChanged change)
        {
            Refresh();
        }

        private void Refresh()
        {
            readout.text = ReadoutFor(progression != null ? progression.Progression : null);
        }

        private static string Number(long value)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
