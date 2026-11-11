using System.Globalization;
using System.Text;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.WorldData.Text;

namespace Mmorpg.Client.Ui
{
    // The texts of the LoginMenu and CharacterSelect screens, without Unity objects.
    public static class AccountEntryPresentation
    {
        public const string Separator = "  ·  ";

        // The limits GameService enforces, so a field cannot take more.
        public const int EmailLimit = 320;
        public const int UsernameLimit = 32;
        public const int PasswordLimit = 128;
        public const int CharacterNameLimit = 24;

        // An identifier as words: "Human_Male" is "Human Male", "DarkBrownHair" is "Dark Brown
        // Hair", and "face" is "Face".
        public static string Words(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return string.Empty;
            }

            var words = new StringBuilder(identifier.Length + 4);
            for (var index = 0; index < identifier.Length; index++)
            {
                var character = identifier[index];
                if (character == '_' || character == '-')
                {
                    character = ' ';
                }
                else if (index > 0
                    && char.IsUpper(character)
                    && (char.IsLower(identifier[index - 1]) || char.IsDigit(identifier[index - 1])))
                {
                    words.Append(' ');
                }

                words.Append(index == 0 ? char.ToUpperInvariant(character) : character);
            }

            return words.ToString();
        }

        // Such as "Underwear (legs)".
        public static string OfferLabel(string kind, string slot)
        {
            return Words(kind) + (string.IsNullOrEmpty(slot) ? string.Empty : " (" + slot + ")");
        }

        // Such as "Human Male  ·  100 gold".
        public static string CharacterDetail(CharacterResponse character, TextCatalog texts)
        {
            if (character == null)
            {
                return string.Empty;
            }

            var body = string.IsNullOrEmpty(character.bodyModel)
                ? MessageArgument.TextKey("body", "ui/characters/unknown-body")
                : MessageArgument.Text("body", Words(character.bodyModel));
            return texts.Format(
                "ui/characters/character-detail",
                body,
                MessageArgument.Text("gold", character.currency.ToString(CultureInfo.InvariantCulture)));
        }

        // Such as "LOCAL  ·  Local Development".
        public static string ShardDetail(ShardResponse shard)
        {
            if (shard == null)
            {
                return string.Empty;
            }

            return string.IsNullOrEmpty(shard.fleetDisplayName)
                ? shard.regionCode ?? string.Empty
                : shard.regionCode + Separator + shard.fleetDisplayName;
        }

        // Such as "Online 3 / 100", or "Offline".
        public static string ShardAvailability(ShardResponse shard, TextCatalog texts)
        {
            return shard != null && shard.isOnline
                ? texts.Format(
                    "ui/characters/shard-online",
                    MessageArgument.Text("players", shard.activePlayers.ToString(CultureInfo.InvariantCulture)),
                    MessageArgument.Text("capacity", shard.capacity.ToString(CultureInfo.InvariantCulture)))
                : texts.Get("ui/characters/shard-offline");
        }

        // Such as "2 characters  ·  1 of 2 shards online".
        public static string SelectionSummary(CharacterSelectState state, TextCatalog texts)
        {
            var online = 0;
            foreach (var shard in state.Shards)
            {
                online += shard.isOnline ? 1 : 0;
            }

            var characters = state.Characters.Count;
            var shards = state.Shards.Count;
            return texts.Format(
                "ui/characters/selection-summary",
                MessageArgument.Text("characters", Count(
                    texts,
                    characters == 1 ? "ui/characters/character-count-one" : "ui/characters/character-count-many",
                    characters)),
                MessageArgument.Text("online", online.ToString(CultureInfo.InvariantCulture)),
                MessageArgument.Text("shards", Count(
                    texts,
                    shards == 1 ? "ui/characters/shard-count-one" : "ui/characters/shard-count-many",
                    shards)));
        }

        // Why Enter World cannot run, or null when it can.
        public static string EnterWorldBlocker(CharacterSelectState state, TextCatalog texts)
        {
            if (state.Characters.Count == 0)
            {
                return texts.Get("ui/characters/create-first");
            }

            if (state.SelectedCharacter == null)
            {
                return texts.Get("ui/characters/choose-character");
            }

            if (state.Shards.Count == 0)
            {
                return texts.Get("ui/characters/no-shards");
            }

            if (state.SelectedShard == null)
            {
                return texts.Get("ui/characters/no-shard-online");
            }

            return null;
        }

        // The client's text for the code of a failed request. A request that never reached
        // GameService names the address it tried instead, and its caller logs the diagnostic.
        public static string ErrorText(ApiError error, string gameServiceBaseUrl, TextCatalog texts)
        {
            if (error == null)
            {
                return string.Empty;
            }

            if (error.Kind == ApiErrorKind.Network || error.Kind == ApiErrorKind.Timeout)
            {
                return texts.Format(
                    "ui/login/service-unreachable",
                    MessageArgument.Text("url", gameServiceBaseUrl),
                    MessageArgument.Text("code", error.Code));
            }

            return ClientMessages.Text(texts, error);
        }

        // A refusal by the worker and a failure that the client found itself both show the
        // client's text for their code; the explanation goes to the log.
        public static string ErrorText(RealtimeClientError error, TextCatalog texts)
        {
            if (error == null)
            {
                return string.Empty;
            }

            return ClientMessages.Text(texts, error.Code, error.Message);
        }

        public static string ConnectionLine(
            string gameServiceBaseUrl,
            int requestTimeoutSeconds,
            int realtimeTimeoutSeconds,
            TextCatalog texts)
        {
            return texts.Format(
                "ui/login/connection",
                MessageArgument.Text("url", gameServiceBaseUrl),
                MessageArgument.Text("request", requestTimeoutSeconds.ToString(CultureInfo.InvariantCulture)),
                MessageArgument.Text("realtime", realtimeTimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        // A count with the text that fits it, such as "1 character" or "2 characters".
        private static string Count(TextCatalog texts, string key, int count)
        {
            return texts.Format(key, MessageArgument.Text("count", count.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
