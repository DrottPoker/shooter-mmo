using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo.Ui
{
    public sealed class CharacterSelectPanel : MonoBehaviour
    {
        private readonly ShooterMmoApiClient apiClient = new ShooterMmoApiClient();

        private string characterName = "Hero One";
        private string status = "Select or create a character, then join a world.";
        private CharacterResponse[] characters = new CharacterResponse[0];
        private WorldResponse[] worlds = new WorldResponse[0];
        private int selectedCharacterIndex;
        private int selectedWorldIndex;
        private bool isBusy;
        private Vector2 scrollPosition;

        private void Start()
        {
            if (!ShooterMmoClientSession.IsAuthenticated)
            {
                status = "No active login session. Return to LoginMenu.";
                return;
            }

            RefreshCharacters();
            RefreshWorlds();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(TemporaryPanelStyles.GetPanelRect(620f, 720f), "Character Select", GUI.skin.window);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);

            DrawAccountSection();
            DrawCharacterSection();
            DrawWorldSection();

            GUILayout.Space(12f);
            TemporaryPanelStyles.DrawStatus(isBusy, status);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawAccountSection()
        {
            GUILayout.Label("Account");

            if (ShooterMmoClientSession.Auth != null)
            {
                GUILayout.Label("Signed in: " + ShooterMmoClientSession.Auth.username);
            }
            else
            {
                GUILayout.Label("Not signed in.");
            }

            if (GUILayout.Button("Back To Login", GUILayout.Height(32f)))
            {
                ShooterMmoClientSession.Clear();
                SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
            }

            GUILayout.Space(12f);
        }

        private void DrawCharacterSection()
        {
            GUILayout.Label("Characters");
            characterName = TemporaryPanelStyles.LabeledTextField("Name", characterName);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create Character", GUILayout.Height(32f)))
            {
                CreateCharacter();
            }

            if (GUILayout.Button("Refresh Characters", GUILayout.Height(32f)))
            {
                RefreshCharacters();
            }
            GUILayout.EndHorizontal();

            if (characters.Length > 0)
            {
                selectedCharacterIndex = Mathf.Clamp(selectedCharacterIndex, 0, characters.Length - 1);
                var labels = new string[characters.Length];
                for (var index = 0; index < characters.Length; index++)
                {
                    labels[index] = characters[index].name;
                }

                selectedCharacterIndex = GUILayout.SelectionGrid(selectedCharacterIndex, labels, 1);
                ShooterMmoClientSession.SelectedCharacter = characters[selectedCharacterIndex];
            }
            else
            {
                GUILayout.Label("No characters loaded.");
            }

            GUILayout.Space(12f);
        }

        private void DrawWorldSection()
        {
            GUILayout.Label("Worlds");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Worlds", GUILayout.Height(32f)))
            {
                RefreshWorlds();
            }

            if (GUILayout.Button("Join Selected World", GUILayout.Height(32f)))
            {
                JoinSelectedWorld();
            }
            GUILayout.EndHorizontal();

            if (worlds.Length > 0)
            {
                selectedWorldIndex = Mathf.Clamp(selectedWorldIndex, 0, worlds.Length - 1);
                var labels = new string[worlds.Length];
                for (var index = 0; index < worlds.Length; index++)
                {
                    labels[index] = worlds[index].displayName + " (" + worlds[index].id + ")";
                }

                selectedWorldIndex = GUILayout.SelectionGrid(selectedWorldIndex, labels, 1);
                ShooterMmoClientSession.SelectedWorld = worlds[selectedWorldIndex];
            }
            else
            {
                GUILayout.Label("No worlds loaded.");
            }
        }

        private void CreateCharacter()
        {
            if (isBusy || !EnsureAuthenticated())
            {
                return;
            }

            var request = new CreateCharacterRequest
            {
                name = characterName
            };

            RunRequest(apiClient.CreateCharacter(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                request,
                character =>
                {
                    status = "Created character " + character.name + ".";
                    RefreshCharacters();
                },
                SetError));
        }

        private void RefreshCharacters()
        {
            if (!EnsureAuthenticated())
            {
                return;
            }

            RunRequest(apiClient.GetCharacters(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                result =>
                {
                    characters = result;
                    selectedCharacterIndex = Mathf.Clamp(selectedCharacterIndex, 0, Mathf.Max(0, characters.Length - 1));
                    if (characters.Length > 0)
                    {
                        ShooterMmoClientSession.SelectedCharacter = characters[selectedCharacterIndex];
                    }

                    status = "Loaded " + characters.Length + " characters.";
                },
                SetError));
        }

        private void RefreshWorlds()
        {
            RunRequest(apiClient.GetWorlds(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                result =>
                {
                    worlds = result;
                    selectedWorldIndex = Mathf.Clamp(selectedWorldIndex, 0, Mathf.Max(0, worlds.Length - 1));
                    if (worlds.Length > 0)
                    {
                        ShooterMmoClientSession.SelectedWorld = worlds[selectedWorldIndex];
                    }

                    status = "Loaded " + worlds.Length + " worlds.";
                },
                SetError));
        }

        private void JoinSelectedWorld()
        {
            if (isBusy || !EnsureAuthenticated())
            {
                return;
            }

            if (characters.Length == 0 || worlds.Length == 0)
            {
                status = "Load a character and a world before joining.";
                return;
            }

            var character = characters[selectedCharacterIndex];
            var world = worlds[selectedWorldIndex];
            ShooterMmoClientSession.SelectedCharacter = character;
            ShooterMmoClientSession.SelectedWorld = world;

            var request = new JoinWorldRequest
            {
                characterId = character.id
            };

            RunRequest(apiClient.CreateJoinTicket(
                ShooterMmoClientSession.AuthServiceBaseUrl,
                ShooterMmoClientSession.SessionToken,
                world.id,
                request,
                join =>
                {
                    var debugJoinRequest = new DebugJoinRequest
                    {
                        joinTicket = join.joinTicket
                    };

                    RunRequest(apiClient.DebugJoinWorldServer(
                        ShooterMmoClientSession.WorldServerBaseUrl,
                        debugJoinRequest,
                        session =>
                        {
                            ShooterMmoClientSession.ActiveWorldSession = session;
                            status = "Joined " + session.worldId + " as " + session.characterName + ".";
                            SceneManager.LoadScene(ShooterMmoSceneNames.WorldScene);
                        },
                        SetError));
                },
                SetError));
        }

        private bool EnsureAuthenticated()
        {
            if (ShooterMmoClientSession.IsAuthenticated)
            {
                return true;
            }

            status = "Return to LoginMenu and login first.";
            return false;
        }

        private void RunRequest(System.Collections.IEnumerator request)
        {
            StartCoroutine(RunRequestRoutine(request));
        }

        private System.Collections.IEnumerator RunRequestRoutine(System.Collections.IEnumerator request)
        {
            isBusy = true;
            yield return request;
            isBusy = false;
        }

        private void SetError(string message)
        {
            status = "Error: " + message;
            Debug.LogWarning(status);
        }
    }
}

