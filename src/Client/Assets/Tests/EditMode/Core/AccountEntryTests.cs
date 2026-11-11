using Mmorpg.Client.Api;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.WorldData.Text;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // The texts and the choices of the LoginMenu and CharacterSelect screens.
    public sealed class AccountEntryTests
    {
        [Test]
        public void IdentifiersReadAsWords()
        {
            Assert.That(AccountEntryPresentation.Words("Human_Male"), Is.EqualTo("Human Male"));
            Assert.That(AccountEntryPresentation.Words("DarkBrownHair"), Is.EqualTo("Dark Brown Hair"));
            Assert.That(AccountEntryPresentation.Words("face"), Is.EqualTo("Face"));
            Assert.That(AccountEntryPresentation.Words("none"), Is.EqualTo("None"));
            Assert.That(AccountEntryPresentation.Words(null), Is.Empty);
            Assert.That(AccountEntryPresentation.OfferLabel("underwear", "legs"), Is.EqualTo("Underwear (legs)"));
            Assert.That(AccountEntryPresentation.OfferLabel("hair", string.Empty), Is.EqualTo("Hair"));
        }

        [Test]
        public void CharactersAndShardsReadAsOneLine()
        {
            var texts = FixtureTexts.Ui();
            var character = new CharacterResponse { name = "Mira", bodyModel = "Human_Female", currency = 100 };
            Assert.That(
                AccountEntryPresentation.CharacterDetail(character, texts),
                Is.EqualTo(texts.Format(
                    "ui/characters/character-detail",
                    MessageArgument.Text("body", "Human Female"),
                    MessageArgument.Text("gold", "100"))));

            var online = Shard("local-shard-1", true);
            Assert.That(
                AccountEntryPresentation.ShardDetail(online),
                Is.EqualTo("LOCAL" + AccountEntryPresentation.Separator + "Local Development"));
            Assert.That(
                AccountEntryPresentation.ShardAvailability(online, texts),
                Is.EqualTo(texts.Format(
                    "ui/characters/shard-online",
                    MessageArgument.Text("players", "3"),
                    MessageArgument.Text("capacity", "100"))));
            Assert.That(
                AccountEntryPresentation.ShardAvailability(Shard("local-shard-2", false), texts),
                Is.EqualTo(texts.Get("ui/characters/shard-offline")));
        }

        // A character without a body model still shows its gold.
        [Test]
        public void ACharacterWithoutABodyModelReadsAsAnUnknownBody()
        {
            var texts = FixtureTexts.Ui();
            var character = new CharacterResponse { name = "Mira", bodyModel = string.Empty, currency = 7 };

            Assert.That(
                AccountEntryPresentation.CharacterDetail(character, texts),
                Is.EqualTo(texts.Format(
                    "ui/characters/character-detail",
                    MessageArgument.Text("body", texts.Get("ui/characters/unknown-body")),
                    MessageArgument.Text("gold", "7"))));
            Assert.That(AccountEntryPresentation.CharacterDetail(null, texts), Is.Empty);
        }

        [Test]
        public void AnUnansweredRequestNamesTheGameServiceAddress()
        {
            var network = new ApiError(ApiErrorKind.Network, 0, "network_error", "Cannot connect to destination host", null);
            var refused = new ApiError(ApiErrorKind.Http, 401, "invalid_credentials", "Login or password is incorrect.", "c-1");

            var texts = TextCatalog.Create(new[]
            {
                FixtureTexts.MessageTable(("invalid_credentials", "The login or the password is incorrect.")),
                FixtureTexts.UiTable()
            });

            Assert.That(
                AccountEntryPresentation.ErrorText(network, "http://localhost:5000", texts),
                Is.EqualTo(texts.Format(
                    "ui/login/service-unreachable",
                    MessageArgument.Text("url", "http://localhost:5000"),
                    MessageArgument.Text("code", "network_error"))));
            Assert.That(
                AccountEntryPresentation.ErrorText(refused, "http://localhost:5000", texts),
                Is.EqualTo("The login or the password is incorrect."));
        }

        // The sentence GameService sends is for the log. The player reads the client's text for
        // the code, and a code without a text shows the code.
        [Test]
        public void ARefusedRequestShowsTheClientsTextForItsCode()
        {
            var texts = FixtureTexts.Messages(("character_name_taken", "That character name is already taken."));
            var listed = new ApiError(ApiErrorKind.Http, 409, "character_name_taken", "Character name is already taken.", "c-1");
            var unlisted = new ApiError(ApiErrorKind.Http, 500, "internal_server_error", "Something broke.", "c-2");

            Assert.That(
                AccountEntryPresentation.ErrorText(listed, "http://localhost:5000", texts),
                Is.EqualTo("That character name is already taken."));
            Assert.That(
                AccountEntryPresentation.ErrorText(unlisted, "http://localhost:5000", texts),
                Is.EqualTo("That did not work. [internal_server_error]"));
        }

        [Test]
        public void ARefusalByTheWorkerShowsTheClientsTextAndAClientFailureKeepsItsOwn()
        {
            var texts = FixtureTexts.Messages(("session_reconnected", "This character connected from another client."));
            var server = new RealtimeClientError(
                RealtimeClientErrorKind.Server,
                "session_reconnected",
                "This character connected from another client. (server words)");
            var local = new RealtimeClientError(
                RealtimeClientErrorKind.Protocol,
                "invalid_join_rejection",
                "The join rejection could not be read.");

            Assert.That(
                AccountEntryPresentation.ErrorText(server, texts),
                Is.EqualTo("This character connected from another client."));
            Assert.That(
                AccountEntryPresentation.ErrorText(local, texts),
                Is.EqualTo("The join rejection could not be read. [invalid_join_rejection]"));
            Assert.That(AccountEntryPresentation.ErrorText((RealtimeClientError)null, texts), Is.Empty);
        }

        [Test]
        public void TheChosenCharacterAndShardSurviveARefresh()
        {
            var texts = FixtureTexts.Ui();
            var state = new CharacterSelectState();
            var changes = 0;
            state.Changed += () => changes++;

            state.SetCharacters(new[] { Character("a"), null, Character("b") }, "b");
            state.SetShards(new[] { Shard("down", false), Shard("one", true), Shard("two", true) }, null);
            Assert.That(state.Characters.Count, Is.EqualTo(2));
            Assert.That(state.SelectedCharacter.id, Is.EqualTo("b"));
            // Without a preferred shard, the first online one.
            Assert.That(state.SelectedShard.id, Is.EqualTo("one"));
            Assert.That(state.CanEnterWorld, Is.True);
            Assert.That(changes, Is.EqualTo(2));

            state.SelectShard("down");
            Assert.That(state.SelectedShard.id, Is.EqualTo("one"), "An offline shard cannot be chosen.");
            state.SelectShard("two");
            state.SelectCharacter("a");

            // A refresh keeps the chosen ones, and a preferred one wins.
            state.SetCharacters(new[] { Character("b"), Character("a") }, null);
            state.SetShards(new[] { Shard("two", true), Shard("one", true) }, null);
            Assert.That(state.SelectedCharacter.id, Is.EqualTo("a"));
            Assert.That(state.SelectedShard.id, Is.EqualTo("two"));
            state.SetCharacters(new[] { Character("b"), Character("a"), Character("c") }, "c");
            Assert.That(state.SelectedCharacter.id, Is.EqualTo("c"));

            // A chosen shard that went offline gives way to an online one; with none, none is chosen.
            state.SetShards(new[] { Shard("two", false), Shard("one", true) }, "two");
            Assert.That(state.SelectedShard.id, Is.EqualTo("one"));
            state.SetShards(new[] { Shard("one", false) }, null);
            Assert.That(state.SelectedShard, Is.Null);
            Assert.That(state.CanEnterWorld, Is.False);
            Assert.That(
                AccountEntryPresentation.EnterWorldBlocker(state, texts),
                Is.EqualTo(texts.Get("ui/characters/no-shard-online")));
        }

        [Test]
        public void CreationOpensWithoutCharactersAndClosesOnAChoice()
        {
            var texts = FixtureTexts.Ui();
            var state = new CharacterSelectState();
            Assert.That(state.IsCreating, Is.False, "Nothing is shown as missing before the list loaded.");

            state.SetCharacters(new CharacterResponse[0], null);
            Assert.That(state.IsCreating, Is.True);
            Assert.That(state.CanCancelCreation, Is.False);
            state.SetCreating(false);
            Assert.That(state.IsCreating, Is.True, "Without a character, creation stays open.");
            Assert.That(
                AccountEntryPresentation.EnterWorldBlocker(state, texts),
                Is.EqualTo(texts.Get("ui/characters/create-first")));

            state.SetCharacters(new[] { Character("a") }, "a");
            Assert.That(state.IsCreating, Is.False);
            state.SetCreating(true);
            Assert.That(state.IsCreating, Is.True);
            Assert.That(state.CanCancelCreation, Is.True);
            state.SelectCharacter("a");
            Assert.That(state.IsCreating, Is.False, "Choosing a character closes creation.");

            var versions = state.CharactersVersion;
            state.SetCharacters(null, null);
            Assert.That(state.CharactersVersion, Is.EqualTo(versions + 1));
            Assert.That(state.Characters, Is.Empty);
            Assert.That(state.SelectedCharacter, Is.Null);
        }

        [Test]
        public void TheSummaryCountsCharactersAndOnlineShards()
        {
            var texts = FixtureTexts.Ui();
            var state = new CharacterSelectState();
            state.SetCharacters(new[] { Character("a") }, null);
            state.SetShards(new[] { Shard("one", true), Shard("two", false) }, null);

            Assert.That(
                AccountEntryPresentation.SelectionSummary(state, texts),
                Is.EqualTo(Summary(texts, "ui/characters/character-count-one", 1, 1, "ui/characters/shard-count-many", 2)));

            // Several characters and one shard pick the other count texts.
            state.SetCharacters(new[] { Character("a"), Character("b") }, null);
            state.SetShards(new[] { Shard("one", false) }, null);
            Assert.That(
                AccountEntryPresentation.SelectionSummary(state, texts),
                Is.EqualTo(Summary(texts, "ui/characters/character-count-many", 2, 0, "ui/characters/shard-count-one", 1)));

            state.SetCharacters(new CharacterResponse[0], null);
            state.SetShards(new ShardResponse[0], null);
            Assert.That(
                AccountEntryPresentation.SelectionSummary(state, texts),
                Is.EqualTo(Summary(texts, "ui/characters/character-count-many", 0, 0, "ui/characters/shard-count-many", 0)));
        }

        private static string Summary(
            TextCatalog texts,
            string charactersKey,
            int characters,
            int online,
            string shardsKey,
            int shards)
        {
            return texts.Format(
                "ui/characters/selection-summary",
                MessageArgument.Text("characters", texts.Format(charactersKey, MessageArgument.Text("count", characters.ToString()))),
                MessageArgument.Text("online", online.ToString()),
                MessageArgument.Text("shards", texts.Format(shardsKey, MessageArgument.Text("count", shards.ToString()))));
        }

        private static CharacterResponse Character(string id)
        {
            return new CharacterResponse { id = id, name = "Character " + id, bodyModel = "Human_Male" };
        }

        private static ShardResponse Shard(string id, bool online)
        {
            return new ShardResponse
            {
                id = id,
                displayName = "Shard " + id,
                regionCode = "LOCAL",
                fleetDisplayName = "Local Development",
                isOnline = online,
                activePlayers = 3,
                capacity = 100
            };
        }
    }
}
