using System;
using System.Collections.Generic;

namespace Mmorpg.Client.Gameplay
{
    // A message the game tells the player, such as an Examine description.
    public readonly struct GameMessage
    {
        public GameMessage(string text, float time)
        {
            Text = text;
            Time = time;
        }

        public string Text { get; }

        // Unscaled seconds since startup when the message arrived.
        public float Time { get; }
    }

    // The newest game messages, oldest first. Presentation decides how long each stays visible.
    public sealed class GameMessageLog
    {
        public const int Capacity = 8;

        private readonly List<GameMessage> messages = new List<GameMessage>(Capacity);

        public event Action Changed;

        public IReadOnlyList<GameMessage> Messages
        {
            get { return messages; }
        }

        public void Add(string text, float time)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (messages.Count == Capacity)
            {
                messages.RemoveAt(0);
            }

            messages.Add(new GameMessage(text.Trim(), time));
            Changed?.Invoke();
        }

        public void Clear()
        {
            if (messages.Count == 0)
            {
                return;
            }

            messages.Clear();
            Changed?.Invoke();
        }
    }
}
