using System.Text;
using Mmorpg.Client.Api;
using Mmorpg.Client.Networking;

namespace Mmorpg.Client.Ui
{
    // The texts of the LoginMenu and CharacterSelect screens, without Unity objects.
    public static class AccountEntryPresentation
    {
        public const string Separator = "  ·  ";

        public const string UsernameHint = "3 to 32 letters, numbers, or underscores.";
        public const string PasswordHint = "8 to 128 characters.";
        public const string CharacterNameHint = "3 to 24 letters, numbers, spaces, hyphens, or underscores.";

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
        public static string CharacterDetail(CharacterResponse character)
        {
            if (character == null)
            {
                return string.Empty;
            }

            var body = string.IsNullOrEmpty(character.bodyModel) ? "Unknown body" : Words(character.bodyModel);
            return body + Separator + character.currency + " gold";
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
        public static string ShardAvailability(ShardResponse shard)
        {
            return shard != null && shard.isOnline
                ? "Online " + shard.activePlayers + " / " + shard.capacity
                : "Offline";
        }

        // Such as "2 characters  ·  1 of 2 shards online".
        public static string SelectionSummary(CharacterSelectState state)
        {
            var online = 0;
            foreach (var shard in state.Shards)
            {
                online += shard.isOnline ? 1 : 0;
            }

            return Count(state.Characters.Count, "character")
                + Separator + online + " of " + Count(state.Shards.Count, "shard") + " online";
        }

        // Why Enter World cannot run, or null when it can.
        public static string EnterWorldBlocker(CharacterSelectState state)
        {
            if (state.Characters.Count == 0)
            {
                return "Create a character first.";
            }

            if (state.SelectedCharacter == null)
            {
                return "Choose a character.";
            }

            if (state.Shards.Count == 0)
            {
                return "GameService lists no shard. Press Refresh once a SimulationWorker runs.";
            }

            if (state.SelectedShard == null)
            {
                return "No shard is online. Start a SimulationWorker, then press Refresh.";
            }

            return null;
        }

        // The message of a refused or failed request with its stable code; a request that never
        // reached GameService names the address it tried.
        public static string ErrorText(ApiError error, string gameServiceBaseUrl)
        {
            if (error == null)
            {
                return string.Empty;
            }

            if (error.Kind == ApiErrorKind.Network || error.Kind == ApiErrorKind.Timeout)
            {
                return "GameService at " + gameServiceBaseUrl + " does not answer. Is it running? ["
                    + error.Code + "]";
            }

            return error.Message + " [" + error.Code + "]";
        }

        public static string ErrorText(RealtimeClientError error)
        {
            return error != null ? error.ToDisplayMessage() : string.Empty;
        }

        public static string ConnectionLine(string gameServiceBaseUrl, int requestTimeoutSeconds, int realtimeTimeoutSeconds)
        {
            return "Game Service: " + gameServiceBaseUrl
                + Separator + "Request Timeout: " + requestTimeoutSeconds + " seconds"
                + Separator + "Realtime Timeout: " + realtimeTimeoutSeconds + " seconds";
        }

        private static string Count(int count, string noun)
        {
            return count + " " + noun + (count == 1 ? string.Empty : "s");
        }
    }
}
