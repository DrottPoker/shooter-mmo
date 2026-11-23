using System;
using System.Collections;
using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.Client.App;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.Client.Worlds;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // The way of the capture of the game views into Development World 2 without a click, as the
    // PlayMode tests of the live stack enter a World: the capture's own account and character,
    // signed in to through the client's own API classes, the selection, and the join through the
    // running client's session and hub, and the load of the World scene, until the World shows the
    // local player. The first capture on a database registers the account and creates its
    // character, and every later one signs in to them again; the capture never changes the
    // character's look, items, or place. The entry screens are not used, so the login, character,
    // and shard they remember stay as they were; AccountEntryKeys keeps them anyway.
    internal static class GameViewLogin
    {
        // The capture's account and character on the local development stack. The password is for
        // the local development stack only, as the development account's is.
        public const string Username = "capture_views";
        public const string Email = "capture_views@example.test";
        public const string Password = "CaptureViews123!";
        public const string CharacterName = "Capture Views";

        // GameService's answer to a login whose account does not exist or whose password is wrong,
        // and to a registration whose name is taken.
        private const string InvalidCredentials = "invalid_credentials";
        private const string AccountExists = "account_already_exists";
        private const float WorldSeconds = 90f;

        // Whether a GameService address is on this computer: an absolute http or https address whose
        // host is the loopback, by name or by number.
        internal static bool IsLocalAddress(string baseUrl)
        {
            return Uri.TryCreate(baseUrl, UriKind.Absolute, out var address)
                && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                && address.IsLoopback;
        }

        // Signs in to the capture's account with its character, made the first time, joins the
        // shard, and loads its World, until the World shows its local player; signedIn gets what it
        // signed in to or made, and entered the World's context.
        public static IEnumerator Enter(ClientBootstrap bootstrap, string shardId, Action<string> phase, Action<string> signedIn,
            Action<WorldSceneContext> entered)
        {
            var session = bootstrap.Session;
            var hub = bootstrap.GetComponent<RealtimeSimulationClient>();
            if (session == null || hub == null)
            {
                throw new InvalidOperationException("The running client has no session or no realtime hub.");
            }

            var api = new ApiClient(10);
            var baseUrl = session.Settings.GameServiceBaseUrl;
            if (!IsLocalAddress(baseUrl))
            {
                // An address override of the Editor may point the client at another stack; the
                // capture's account and its password are for the local one only.
                throw new InvalidOperationException("The client's GameService address " + baseUrl + " is not on this computer. The capture "
                    + "signs in only to the local development stack; clear the GameService address override of the Editor and run it again.");
            }

            phase("account");
            AuthResponse account = null;
            ApiError error = null;
            var registered = false;
            var created = false;
            yield return WithBusyRetry(
                (done, failed) => api.Login(baseUrl, new LoginAccountRequest { login = Username, password = Password }, done, failed),
                value => account = value,
                value => error = value);
            if (account == null)
            {
                if (error == null || error.Code != InvalidCredentials)
                {
                    throw Failure(error, "signing in to the capture's account " + Username);
                }

                // No such account yet: the first capture on this database makes it.
                error = null;
                yield return WithBusyRetry(
                    (done, failed) => api.Register(baseUrl, new RegisterAccountRequest { email = Email, username = Username, password = Password }, done, failed),
                    value => account = value,
                    value => error = value);
                if (account == null)
                {
                    throw Failure(error, error != null && error.Code == AccountExists
                        ? "signing in to the capture's account " + Username + ", which exists with another password,"
                        : "registering the capture's account " + Username);
                }

                registered = true;
            }

            CharacterResponse[] characters = null;
            yield return api.GetCharacters(baseUrl, account.sessionToken, value => characters = value, value => error = value);
            ThrowOn(error, "listing the characters of the capture's account");
            characters = characters ?? new CharacterResponse[0];
            var character = Array.Find(characters, candidate => candidate.name == CharacterName) ?? (characters.Length > 0 ? characters[0] : null);
            if (character == null)
            {
                CharacterAppearanceCatalogResponse appearance = null;
                yield return api.GetCharacterAppearanceCatalog(baseUrl, account.sessionToken, value => appearance = value, value => error = value);
                ThrowOn(error, "reading what character creation offers");
                yield return api.CreateCharacter(
                    baseUrl,
                    account.sessionToken,
                    new CreateCharacterRequest { name = CharacterName, appearance = new CharacterCreationLook(appearance).ToRequest() },
                    value => character = value,
                    value => error = value);
                ThrowOn(error, "creating the capture's character " + CharacterName);
                created = true;
            }

            signedIn((registered ? "registered " : "signed in to ") + Username + (created ? " and created " : " with ") + character.name);

            ShardResponse[] shards = null;
            yield return api.GetShards(baseUrl, value => shards = value, value => error = value);
            ThrowOn(error, "listing the shards");
            var shard = Array.Find(shards ?? new ShardResponse[0], candidate => candidate.id == shardId);
            if (shard == null || !shard.isOnline)
            {
                throw new InvalidOperationException("GameService lists no online shard " + shardId + "; start the default worker.");
            }

            if (!WorldSceneCatalog.TryResolveScene(shard.worldId, out var sceneName, out var sceneError))
            {
                throw new InvalidOperationException(sceneError);
            }

            phase("join");
            session.SignIn(account);
            session.Select(character, shard);
            WorldJoinResult join = null;
            yield return new WorldJoin(session, api, hub.Session).Run(character.id, character.name, shard.id, shard.worldId, ticket => true,
                value => join = value);
            if (join == null || join.Status != WorldJoinStatus.Joined)
            {
                throw new InvalidOperationException("The join of " + shardId + " failed: " + (join != null ? join.Status.ToString() : "no result")
                    + (join != null && join.TicketError != null ? ", " + join.TicketError.ToDiagnostic() : string.Empty)
                    + (join != null && join.JoinError != null ? ", " + join.JoinError.ToDiagnostic() : string.Empty) + ".");
            }

            session.EnterWorld(join.Session);
            phase("world");
            ClientSceneLoader.LoadWorldScene(sceneName);
            var deadline = Time.realtimeSinceStartup + WorldSeconds;
            while (true)
            {
                var context = Object.FindAnyObjectByType<WorldSceneContext>();
                if (context != null && context.LocalPlayer != null && context.LocalPlayer.IsServerAuthoritative && context.IsWorldShown)
                {
                    entered(context);
                    yield break;
                }

                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new TimeoutException("The World " + sceneName + " did not show its local player within " + WorldSeconds + " s.");
                }

                yield return null;
            }
        }

        // Sends an account request, a login or a registration, and waits out GameService's
        // auth_busy as the login screen does: the answer's Retry-After, then the request again.
        private static IEnumerator WithBusyRetry(Func<Action<AuthResponse>, Action<ApiError>, IEnumerator> send, Action<AuthResponse> done,
            Action<ApiError> failed)
        {
            var retry = new AuthBusyRetry();
            while (true)
            {
                AuthResponse account = null;
                ApiError error = null;
                yield return send(value => account = value, value => error = value);
                if (account != null)
                {
                    done(account);
                    yield break;
                }

                if (error == null || !retry.TrySchedule(error, Time.realtimeSinceStartupAsDouble))
                {
                    failed(error);
                    yield break;
                }

                yield return new WaitForSecondsRealtime(AuthBusyRetry.DelaySeconds(error));
                retry.BeginAttempt();
            }
        }

        private static void ThrowOn(ApiError error, string what)
        {
            if (error != null)
            {
                throw Failure(error, what);
            }
        }

        private static Exception Failure(ApiError error, string what)
        {
            return new InvalidOperationException(what + " failed: " + (error != null ? error.ToDiagnostic() : "no answer") + ".");
        }
    }

    // The keys of the account entry screens that the Editors of a computer share in PlayerPrefs: the
    // last login, character, and shard. The capture takes them as it finds them and puts them back
    // when it ends, so a person's next Play Mode starts with their own.
    internal sealed class AccountEntryKeys
    {
        public static readonly string[] Names =
        {
            "Mmorpg.Account.LastLogin",
            "Mmorpg.Account.LastCharacter",
            "Mmorpg.Account.LastShard",
        };

        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        public static AccountEntryKeys Take()
        {
            var keys = new AccountEntryKeys();
            foreach (var name in Names)
            {
                keys.values[name] = PlayerPrefs.HasKey(name) ? PlayerPrefs.GetString(name, string.Empty) : null;
            }

            return keys;
        }

        // Whether a key holds another value than it was taken with.
        public bool Changed
        {
            get
            {
                foreach (var name in Names)
                {
                    var now = PlayerPrefs.HasKey(name) ? PlayerPrefs.GetString(name, string.Empty) : null;
                    if (!string.Equals(now, values[name], StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // Puts back every key that changed; true when one did.
        public bool Restore()
        {
            if (!Changed)
            {
                return false;
            }

            foreach (var name in Names)
            {
                if (values[name] == null)
                {
                    PlayerPrefs.DeleteKey(name);
                }
                else
                {
                    PlayerPrefs.SetString(name, values[name]);
                }
            }

            PlayerPrefs.Save();
            return true;
        }
    }
}
