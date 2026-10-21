using System;

namespace Mmorpg.Client.Gameplay
{
    // Whether player bodies are tinted by who they are: purple for the local player, orange
    // for another person, and blue for a development bot. A debug setting, off by default;
    // the World Debug panel switches it and the composition root keeps it between sessions.
    public sealed class PlayerBodyTintSetting
    {
        private bool enabled;

        public PlayerBodyTintSetting(bool enabled)
        {
            this.enabled = enabled;
        }

        public event Action Changed;

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value)
                {
                    return;
                }

                enabled = value;
                Changed?.Invoke();
            }
        }
    }
}
