using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Items
{
    // Names who left a corpse. A character's corpse carries the name the character had, and
    // a Mob's corpse carries its actor definition, which the text catalog names.
    public static class CorpseSourceText
    {
        public static string Name(RealtimeCorpseSource source, TextCatalog texts)
        {
            if (source == null)
            {
                return string.Empty;
            }

            if (!source.IsActor)
            {
                return source.CharacterName ?? string.Empty;
            }

            return texts != null && TextKeys.IsValidId(source.ActorDefinitionId)
                ? texts.Get(TextKeys.ActorName(source.ActorDefinitionId))
                : source.ActorDefinitionId;
        }

        public static bool AreSame(RealtimeCorpseSource first, RealtimeCorpseSource second)
        {
            if (first == null || second == null)
            {
                return first == second;
            }

            return string.Equals(first.CharacterName, second.CharacterName, System.StringComparison.Ordinal)
                && string.Equals(first.ActorDefinitionId, second.ActorDefinitionId, System.StringComparison.Ordinal);
        }
    }
}
