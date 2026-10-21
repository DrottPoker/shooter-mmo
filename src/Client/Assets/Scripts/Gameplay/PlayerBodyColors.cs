using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // The colors that tell players apart when bodies are tinted: the local player is purple,
    // another person orange, and a development bot blue, so none can be taken for another.
    public static class PlayerBodyColors
    {
        public static readonly Color LocalPlayer = new Color(0.656f, 0.328f, 0.953f, 1f);
        public static readonly Color Player = new Color(1f, 0.55f, 0.15f, 1f);
        public static readonly Color DevelopmentBot = new Color(0.29f, 0.70f, 1f, 1f);

        public static Color ForRemote(string archetypeId)
        {
            return string.Equals(archetypeId, RealtimePlayerArchetypeIds.DevelopmentBot, System.StringComparison.Ordinal)
                ? DevelopmentBot
                : Player;
        }
    }
}
