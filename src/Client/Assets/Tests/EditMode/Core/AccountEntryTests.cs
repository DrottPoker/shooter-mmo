using Mmorpg.Client.Api;
using Mmorpg.Client.Ui;
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
            var character = new CharacterResponse { name = "Mira", bodyModel = "Human_Female", currency = 100 };
            Assert.That(
                AccountEntryPresentation.CharacterDetail(character),
                Is.EqualTo("Human Female" + AccountEntryPresentation.Separator + "100 gold"));

            var online = Shard("local-shard-1", true);
            Assert.That(
                AccountEntryPresentation.ShardDetail(online),
                Is.EqualTo("LOCAL" + AccountEntryPresentation.Separator + "Local Development"));
            Assert.That(AccountEntryPresentation.ShardAvailability(online), Is.EqualTo("Online 3 / 100"));
            Assert.That(AccountEntryPresentation.ShardAvailability(Shard("local-shard-2", false)), Is.EqualTo("Offline"));
        }

        [Test]
        public void AnUnansweredRequestNamesTheGameServiceAddress()
        {
            var network = new ApiError(ApiErrorKind.Network, 0, "network_error", "Cannot connect to destination host", null);
            var refused = new ApiError(ApiErrorKind.Http, 401, "invalid_credentials", "Login or password is incorrect.", "c-1");

            Assert.That(
                AccountEntryPresentation.ErrorText(network, "http://localhost:5000"),
                Is.EqualTo("GameService at http://localhost:5000 does not answer. Is it running? [network_error]"));
            Assert.That(
                AccountEntryPresentation.ErrorText(refused, "http://localhost:5000"),
                Is.EqualTo("Login or password is incorrect. [invalid_credentials]"));
        }

        [Test]
        public void TheChosenCharacterAndShardSurviveARefresh()
        {
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
                AccountEntryPresentation.EnterWorldBlocker(state),
                Is.EqualTo("No shard is online. Start a SimulationWorker, then press Refresh."));
        }

        [Test]
        public void CreationOpensWithoutCharactersAndClosesOnAChoice()
        {
            var state = new CharacterSelectState();
            Assert.That(state.IsCreating, Is.False, "Nothing is shown as missing before the list loaded.");

            state.SetCharacters(new CharacterResponse[0], null);
            Assert.That(state.IsCreating, Is.True);
            Assert.That(state.CanCancelCreation, Is.False);
            state.SetCreating(false);
            Assert.That(state.IsCreating, Is.True, "Without a character, creation stays open.");
            Assert.That(AccountEntryPresentation.EnterWorldBlocker(state), Is.EqualTo("Create a character first."));

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
            var state = new CharacterSelectState();
            state.SetCharacters(new[] { Character("a") }, null);
            state.SetShards(new[] { Shard("one", true), Shard("two", false) }, null);

            Assert.That(
                AccountEntryPresentation.SelectionSummary(state),
                Is.EqualTo("1 character" + AccountEntryPresentation.Separator + "1 of 2 shards online"));
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
