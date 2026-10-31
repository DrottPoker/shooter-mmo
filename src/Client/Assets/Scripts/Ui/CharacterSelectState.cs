using System;
using System.Collections.Generic;
using Mmorpg.Client.Api;

namespace Mmorpg.Client.Ui
{
    // What CharacterSelect shows: the account's characters and the shards, the chosen one of each
    // kept by id across a refresh, what character creation offers, and whether the creation view
    // is open. Only an online shard can be chosen. Without a character, creation is always open.
    public sealed class CharacterSelectState
    {
        private CharacterResponse[] characters = new CharacterResponse[0];
        private ShardResponse[] shards = new ShardResponse[0];
        private bool creating;

        // Raised after the lists, a choice, the creation offers, or the creation view changed.
        public event Action Changed;

        public IReadOnlyList<CharacterResponse> Characters
        {
            get { return characters; }
        }

        public IReadOnlyList<ShardResponse> Shards
        {
            get { return shards; }
        }

        public bool CharactersLoaded { get; private set; }

        // Counts the lists set, so a view builds its rows again only for a new list.
        public int CharactersVersion { get; private set; }

        public int ShardsVersion { get; private set; }

        public CharacterResponse SelectedCharacter { get; private set; }

        public ShardResponse SelectedShard { get; private set; }

        public CharacterCreationLook CreationLook { get; private set; }

        public bool IsCreating
        {
            get { return creating || (CharactersLoaded && characters.Length == 0); }
        }

        public bool CanCancelCreation
        {
            get { return characters.Length > 0; }
        }

        public bool CanEnterWorld
        {
            get { return SelectedCharacter != null && SelectedShard != null; }
        }

        // Chooses the preferred character when it is listed, else keeps the chosen one, else the
        // first.
        public void SetCharacters(CharacterResponse[] value, string preferredId)
        {
            characters = WithoutNulls(value);
            CharactersLoaded = true;
            CharactersVersion++;
            SelectedCharacter = Find(characters, preferredId)
                ?? Find(characters, SelectedCharacter?.id)
                ?? (characters.Length > 0 ? characters[0] : null);
            Changed?.Invoke();
        }

        // Chooses the preferred shard when it is online, else keeps the chosen one while it is
        // online, else the first online shard.
        public void SetShards(ShardResponse[] value, string preferredId)
        {
            shards = WithoutNulls(value);
            ShardsVersion++;
            SelectedShard = Online(Find(shards, preferredId))
                ?? Online(Find(shards, SelectedShard?.id))
                ?? FirstOnline(shards);
            Changed?.Invoke();
        }

        // Choosing a character closes the creation view.
        public void SelectCharacter(string id)
        {
            var found = Find(characters, id);
            if (found == null || (found == SelectedCharacter && !creating))
            {
                return;
            }

            SelectedCharacter = found;
            creating = false;
            Changed?.Invoke();
        }

        public void SelectShard(string id)
        {
            var found = Online(Find(shards, id));
            if (found == null || found == SelectedShard)
            {
                return;
            }

            SelectedShard = found;
            Changed?.Invoke();
        }

        public void SetCreationLook(CharacterCreationLook look)
        {
            CreationLook = look;
            Changed?.Invoke();
        }

        public void SetCreating(bool value)
        {
            if (creating == value)
            {
                return;
            }

            creating = value;
            Changed?.Invoke();
        }

        private static T[] WithoutNulls<T>(T[] value)
            where T : class
        {
            var kept = new List<T>(value != null ? value.Length : 0);
            foreach (var item in value ?? new T[0])
            {
                if (item != null)
                {
                    kept.Add(item);
                }
            }

            return kept.ToArray();
        }

        private static CharacterResponse Find(CharacterResponse[] list, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (var character in list)
            {
                if (string.Equals(character.id, id, StringComparison.Ordinal))
                {
                    return character;
                }
            }

            return null;
        }

        private static ShardResponse Find(ShardResponse[] list, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (var shard in list)
            {
                if (string.Equals(shard.id, id, StringComparison.Ordinal))
                {
                    return shard;
                }
            }

            return null;
        }

        private static ShardResponse Online(ShardResponse shard)
        {
            return shard != null && shard.isOnline ? shard : null;
        }

        private static ShardResponse FirstOnline(ShardResponse[] list)
        {
            foreach (var shard in list)
            {
                if (shard.isOnline)
                {
                    return shard;
                }
            }

            return null;
        }
    }
}
