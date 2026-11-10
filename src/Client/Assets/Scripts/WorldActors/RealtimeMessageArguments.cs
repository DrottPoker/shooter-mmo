using System.Collections.Generic;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.WorldActors
{
    // The named values that a message carries next to its code, as the text templates take them.
    public static class RealtimeMessageArguments
    {
        public static IReadOnlyList<MessageArgument> ToText(IReadOnlyList<RealtimeMessageArgument> arguments)
        {
            if (arguments == null || arguments.Count == 0)
            {
                return null;
            }

            var values = new MessageArgument[arguments.Count];
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                values[index] = argument.Kind == RealtimeMessageArgumentKind.TextKey
                    ? MessageArgument.TextKey(argument.Name, argument.Value)
                    : MessageArgument.Text(argument.Name, argument.Value);
            }

            return values;
        }
    }
}
