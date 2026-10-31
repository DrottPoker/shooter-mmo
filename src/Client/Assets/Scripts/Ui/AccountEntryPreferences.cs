using UnityEngine;

namespace Mmorpg.Client.Ui
{
    // What the account entry screens remember on this computer: the last login name, and the
    // character and shard of the last Enter World, so the next visit starts with them chosen.
    // Never a password or a token.
    public static class AccountEntryPreferences
    {
        private const string LastLoginKey = "Mmorpg.Account.LastLogin";
        private const string LastCharacterKey = "Mmorpg.Account.LastCharacter";
        private const string LastShardKey = "Mmorpg.Account.LastShard";

        public static string LastLogin
        {
            get { return PlayerPrefs.GetString(LastLoginKey, string.Empty); }
        }

        // A character id of another account matches none of the listed characters.
        public static string LastCharacterId
        {
            get { return PlayerPrefs.GetString(LastCharacterKey, string.Empty); }
        }

        public static string LastShardId
        {
            get { return PlayerPrefs.GetString(LastShardKey, string.Empty); }
        }

        public static void RememberLogin(string login)
        {
            PlayerPrefs.SetString(LastLoginKey, login ?? string.Empty);
            PlayerPrefs.Save();
        }

        public static void RememberWorldEntry(string characterId, string shardId)
        {
            PlayerPrefs.SetString(LastCharacterKey, characterId ?? string.Empty);
            PlayerPrefs.SetString(LastShardKey, shardId ?? string.Empty);
            PlayerPrefs.Save();
        }
    }
}
