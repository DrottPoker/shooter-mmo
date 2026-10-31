using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Draws the LoginMenu and CharacterSelect screens without a GameService.
    public sealed class AccountEntryScreensPlayModeTests
    {
        // Port 9 refuses connections, so no request can reach a real GameService.
        private const string UnreachableGameServiceUrl = "http://127.0.0.1:9";
        private const float RequestWaitSeconds = 15f;

        private string previousBaseUrl;

        [SetUp]
        public void SetUp()
        {
            previousBaseUrl = ClientSession.GameServiceBaseUrl;
            ClientSession.Clear();
            ConfigureGameServiceUrl(UnreachableGameServiceUrl);
        }

        [TearDown]
        public void TearDown()
        {
            ClientSession.Clear();
            ConfigureGameServiceUrl(previousBaseUrl);
        }

        [UnityTest]
        public IEnumerator LoginShowsWhyAnUnansweredRequestFailedAndEnablesTheFormAgain()
        {
            yield return LoadScene(SceneNames.LoginMenu);
            var panel = UnityEngine.Object.FindAnyObjectByType<LoginMenuPanel>();
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.IsRegistering, Is.False);
            Assert.That(VisibleFields(panel), Has.Length.EqualTo(2), "Log In asks for a login and a password.");
            Assert.That(UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude), Has.Length.EqualTo(1));

            panel.ShowRegistration(true);
            Assert.That(VisibleFields(panel), Has.Length.EqualTo(3), "Create Account asks for an email, a username, and a password.");
            panel.ShowRegistration(false);

            LogAssert.Expect(LogType.Error, new Regex("Login failed: network_error"));
            panel.Submit();
            Assert.That(panel.IsBusy, Is.True);
            Assert.That(panel.GetComponentInChildren<CanvasGroup>().interactable, Is.False);
            yield return WaitUntilIdle(() => panel.IsBusy);

            Assert.That(
                panel.StatusText,
                Is.EqualTo("GameService at " + UnreachableGameServiceUrl + " does not answer. Is it running? [network_error]"));
            Assert.That(panel.GetComponentInChildren<CanvasGroup>().interactable, Is.True);
            Assert.That(ClientSession.IsAuthenticated, Is.False);
            Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(SceneNames.LoginMenu));
        }

        [UnityTest]
        public IEnumerator CharacterSelectShowsCreationWithoutCharactersAndTheShardsOtherwise()
        {
            ClientSession.Auth = new AuthResponse
            {
                accountId = Guid.NewGuid().ToString(),
                username = "screen_probe",
                sessionToken = "unreachable-token"
            };
            LogAssert.Expect(LogType.Error, new Regex("API request failed: network_error"));
            yield return LoadScene(SceneNames.CharacterSelect);
            var panel = UnityEngine.Object.FindAnyObjectByType<CharacterSelectPanel>();
            Assert.That(panel, Is.Not.Null);
            yield return WaitUntilIdle(() => panel.IsBusy);
            StringAssert.Contains("does not answer", panel.StatusText);

            var state = panel.State;
            state.SetShards(new[] { Shard("local-shard-1", true), Shard("local-shard-2", false) }, null);
            state.SetCreationLook(new CharacterCreationLook(Catalog()));
            state.SetCharacters(new CharacterResponse[0], null);
            yield return null;

            var createPage = Page(panel, "CreatePage");
            Assert.That(createPage.activeInHierarchy, Is.True);
            Assert.That(Page(panel, "EnterPage").activeInHierarchy, Is.False);
            Assert.That(ButtonLabels(createPage), Does.Contain("Human Male").And.Contain("Human Female"));
            Assert.That(ButtonLabels(createPage), Does.Not.Contain("Cancel"), "Without a character there is nothing to go back to.");
            var nameField = createPage.GetComponentInChildren<InputField>();
            Assert.That(EventSystem.current.currentSelectedGameObject, Is.SameAs(nameField.gameObject));

            var first = Character("Mira");
            var second = Character("Torvald");
            state.SetCharacters(new[] { first, second }, second.id);
            yield return null;

            var enterPage = Page(panel, "EnterPage");
            Assert.That(enterPage.activeInHierarchy, Is.True);
            Assert.That(createPage.activeInHierarchy, Is.False);
            Assert.That(ClientSession.SelectedCharacter, Is.SameAs(state.SelectedCharacter));
            Assert.That(state.SelectedCharacter.id, Is.EqualTo(second.id));
            Assert.That(ClientSession.SelectedShard.id, Is.EqualTo("local-shard-1"));

            var shardRows = Rows(enterPage);
            Assert.That(shardRows.Select(row => row.interactable), Is.EqualTo(new[] { true, false }), "An offline shard cannot be chosen.");
            Assert.That(Rows(Page(panel, "CharactersCard")), Has.Length.EqualTo(2));
            var enterWorld = enterPage.GetComponentsInChildren<Button>().Single(button => Label(button) == "Enter World");
            Assert.That(enterWorld.interactable, Is.True);

            state.SetShards(new[] { Shard("local-shard-1", false) }, null);
            yield return null;
            Assert.That(enterWorld.interactable, Is.False);
            Assert.That(
                enterPage.GetComponentsInChildren<Text>().Select(text => text.text),
                Does.Contain("No shard is online. Start a SimulationWorker, then press Refresh."));
        }

        private static IEnumerator LoadScene(string sceneName)
        {
            var load = SceneManager.LoadSceneAsync(sceneName);
            while (!load.isDone)
            {
                yield return null;
            }

            // Start builds the screen on the next frame.
            yield return null;
        }

        private static IEnumerator WaitUntilIdle(Func<bool> busy)
        {
            var deadline = Time.realtimeSinceStartup + RequestWaitSeconds;
            while (busy() && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(busy(), Is.False, "The request did not end.");
            yield return null;
        }

        private static InputField[] VisibleFields(Component panel)
        {
            return panel.GetComponentsInChildren<InputField>().Where(field => field.gameObject.activeInHierarchy).ToArray();
        }

        private static GameObject Page(Component panel, string name)
        {
            return panel.GetComponentsInChildren<Transform>(true).Single(child => child.name == name).gameObject;
        }

        private static Button[] Rows(GameObject parent)
        {
            return parent.GetComponentsInChildren<Button>().Where(button => button.name == "ListRow").ToArray();
        }

        private static string[] ButtonLabels(GameObject parent)
        {
            return parent.GetComponentsInChildren<Button>().Select(Label).ToArray();
        }

        private static string Label(Button button)
        {
            var text = button.GetComponentInChildren<Text>();
            return text != null ? text.text : string.Empty;
        }

        private static CharacterResponse Character(string name)
        {
            return new CharacterResponse { id = Guid.NewGuid().ToString(), name = name, bodyModel = "Human_Male", currency = 100 };
        }

        private static ShardResponse Shard(string id, bool online)
        {
            return new ShardResponse
            {
                id = id,
                displayName = id,
                worldId = "development-world-2",
                regionCode = "LOCAL",
                fleetDisplayName = "Local Development",
                isOnline = online,
                activePlayers = 0,
                capacity = 100
            };
        }

        private static CharacterAppearanceCatalogResponse Catalog()
        {
            return new CharacterAppearanceCatalogResponse
            {
                kinds = new[] { new CharacterAppearanceKindResponse { id = "face", required = true } },
                bodyModels = new[]
                {
                    new CharacterBodyModelResponse
                    {
                        id = "Human_Male",
                        offers = new[] { new CharacterAppearanceOfferResponse { kind = "face", slot = string.Empty, options = new[] { "Stubble", "Clean" } } }
                    },
                    new CharacterBodyModelResponse
                    {
                        id = "Human_Female",
                        offers = new[] { new CharacterAppearanceOfferResponse { kind = "face", slot = string.Empty, options = new[] { "Blush" } } }
                    },
                },
            };
        }

        private static void ConfigureGameServiceUrl(string baseUrl)
        {
            var config = ScriptableObject.CreateInstance<ClientConfig>();
            JsonUtility.FromJsonOverwrite("{\"gameServiceBaseUrl\":\"" + baseUrl + "\"}", config);
            ClientSession.Configure(config);
            UnityEngine.Object.Destroy(config);
        }
    }
}
