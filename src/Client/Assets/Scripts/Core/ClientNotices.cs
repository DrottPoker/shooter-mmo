using System;

namespace Mmorpg.Client.Core
{
    // What the next entry screen tells the player: why the client left its World or its account
    // session, and how long a character that left still stands in the World. The bootstrap owns
    // one, so it outlives the scene change: the ways out of a World and the forced sign-out post
    // to it, and LoginMenu and CharacterSelect read it when they are composed.
    public sealed class ClientNotices
    {
        private readonly Func<float> clock;
        private string bodyCharacterId;
        private float bodyEndsAt;

        public ClientNotices(Func<float> realtimeClock)
        {
            clock = realtimeClock ?? throw new ArgumentNullException(nameof(realtimeClock));
        }

        // The code of what happened, whose text the next entry screen shows; empty for none.
        public string PendingCode { get; private set; } = string.Empty;

        // A newer notice replaces one that no screen showed yet.
        public void Post(string code)
        {
            PendingCode = code ?? string.Empty;
        }

        // The code to show once, or an empty string; the notice is gone afterwards.
        public string Take()
        {
            var code = PendingCode;
            PendingCode = string.Empty;
            return code;
        }

        // The worker keeps the character's body at least this many seconds after a leave. A hit
        // extends it on the server without telling the client, so the time is a lower bound.
        public void RememberBody(string characterId, int seconds)
        {
            if (string.IsNullOrWhiteSpace(characterId) || seconds <= 0)
            {
                ForgetBody();
                return;
            }

            bodyCharacterId = characterId;
            bodyEndsAt = clock() + seconds;
        }

        public void ForgetBody()
        {
            bodyCharacterId = null;
            bodyEndsAt = 0f;
        }

        // The character whose body still stands and the whole seconds left, rounded up. False once
        // the time is up, and nothing more is assumed then.
        public bool TryGetBody(out string characterId, out int secondsLeft)
        {
            characterId = null;
            secondsLeft = 0;
            if (bodyCharacterId == null)
            {
                return false;
            }

            var left = bodyEndsAt - clock();
            if (left <= 0f)
            {
                ForgetBody();
                return false;
            }

            characterId = bodyCharacterId;
            secondsLeft = (int)Math.Ceiling(left);
            return true;
        }
    }
}
