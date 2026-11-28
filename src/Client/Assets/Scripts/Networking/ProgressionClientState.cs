using System;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.GameProtocol;

namespace Mmorpg.Client.Networking
{
    // The controlled character's level and experience for the World session, as the worker sent
    // them. Before the first progression of a session it holds only the level of the selected
    // character from the character list. Every progression from the worker replaces it and raises
    // Changed once with the message; a reset raises Changed with null. The client never computes a
    // level or an experience value: everything here is what OwnerProgressionChanged carried.
    public sealed class ProgressionClientState : IDisposable
    {
        private readonly IRealtimeProgressionChannel channel;
        private readonly IRealtimeSession session;
        private readonly IClientSession clientSession;
        private string observedCharacterId = string.Empty;
        private int fallbackLevel = 1;

        // A state without a source, for tests that apply progressions by hand.
        public ProgressionClientState()
        {
        }

        // A state that follows the progression channel, resets with every session, and takes the
        // level of the selected character until the first progression arrives.
        public ProgressionClientState(
            IRealtimeProgressionChannel progression,
            IRealtimeSession realtimeSession,
            IClientSession accountSession)
        {
            channel = progression ?? throw new ArgumentNullException(nameof(progression));
            session = realtimeSession ?? throw new ArgumentNullException(nameof(realtimeSession));
            clientSession = accountSession ?? throw new ArgumentNullException(nameof(accountSession));
            ObserveSelection();
            Progression = channel.Progression;
            channel.ProgressionChanged += Apply;
            session.SessionJoined += Reset;
            session.SessionEnded += OnSessionEnded;
            clientSession.SignedIn += OnSelectionChanged;
            clientSession.SignedOut += OnSelectionChanged;
            clientSession.SelectionChanged += OnSelectionChanged;
        }

        // Raised once for every change: with the worker's message, or with null when the state
        // forgot its progression or the selected character's level changed.
        public event Action<RealtimeOwnerProgressionChanged> Changed;

        // The latest progression of the session, or null before the first one.
        public RealtimeOwnerProgressionChanged Progression { get; private set; }

        public bool HasProgression
        {
            get { return Progression != null; }
        }

        // The worker's level, or the selected character's before the first progression.
        public int Level
        {
            get { return Progression != null ? Progression.Level : fallbackLevel; }
        }

        public void Dispose()
        {
            if (channel == null)
            {
                return;
            }

            channel.ProgressionChanged -= Apply;
            session.SessionJoined -= Reset;
            session.SessionEnded -= OnSessionEnded;
            clientSession.SignedIn -= OnSelectionChanged;
            clientSession.SignedOut -= OnSelectionChanged;
            clientSession.SelectionChanged -= OnSelectionChanged;
        }

        // Replaces the state with the worker's progression.
        public void Apply(RealtimeOwnerProgressionChanged change)
        {
            Progression = change ?? throw new ArgumentNullException(nameof(change));
            Changed?.Invoke(change);
        }

        // Forgets the progression of the session. The level it reached stays the selected
        // character's level, so a lost connection that is restored never shows an older level.
        public void Reset()
        {
            if (Progression == null)
            {
                return;
            }

            fallbackLevel = Math.Max(1, (int)Progression.Level);
            Progression = null;
            Changed?.Invoke(null);
        }

        // The level of a character from the character list, which holds until the first
        // progression; a lower bound of 1.
        public void SetSelectedCharacterLevel(int level)
        {
            var before = Level;
            fallbackLevel = Math.Max(1, level);
            if (Level != before)
            {
                Changed?.Invoke(null);
            }
        }

        private void OnSessionEnded(RealtimeClientError error)
        {
            Reset();
        }

        // Another account or character starts over from its own level in the character list.
        private void OnSelectionChanged()
        {
            var character = clientSession.SelectedCharacter;
            var characterId = character?.id ?? string.Empty;
            if (!string.Equals(characterId, observedCharacterId, StringComparison.Ordinal))
            {
                var before = Level;
                var hadProgression = Progression != null;
                ObserveSelection();
                Progression = null;
                if (hadProgression || Level != before)
                {
                    Changed?.Invoke(null);
                }

                return;
            }

            // The same character from a newer character list.
            if (Progression == null && character != null)
            {
                SetSelectedCharacterLevel(character.level);
            }
        }

        private void ObserveSelection()
        {
            var character = clientSession.SelectedCharacter;
            observedCharacterId = character?.id ?? string.Empty;
            fallbackLevel = LevelOf(character);
        }

        private static int LevelOf(CharacterResponse character)
        {
            return character != null ? Math.Max(1, character.level) : 1;
        }
    }
}
