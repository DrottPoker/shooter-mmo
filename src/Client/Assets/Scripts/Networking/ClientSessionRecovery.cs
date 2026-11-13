using System;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Networking
{
    // Owns the forced sign-out: when GameService no longer accepts the account session, or the
    // worker closed the connection because the account logged in from another client, it closes
    // the connection, signs the client session out, and returns to LoginMenu. The bootstrap
    // creates one and passes it to every component that reads GameService.
    public sealed class ClientSessionRecovery
    {
        public const string AccountSessionReplacedCode = "account_session_replaced";

        private const string ReplacedMessage =
            "This client was disconnected because the account logged in from another client. ";

        private readonly ClientSession clientSession;
        private readonly IRealtimeSession realtimeSession;

        public ClientSessionRecovery(ClientSession session, IRealtimeSession realtime)
        {
            clientSession = session ?? throw new ArgumentNullException(nameof(session));
            realtimeSession = realtime ?? throw new ArgumentNullException(nameof(realtime));
        }

        // True when the error was a 401 and the client returns to LoginMenu.
        public bool ReturnToLoginIfUnauthorized(ApiError error)
        {
            if (error == null || !error.IsUnauthorized)
            {
                return false;
            }

            var wasReplaced = string.Equals(
                error.Code,
                AccountSessionReplacedCode,
                StringComparison.Ordinal);
            ClientLog.Error(
                ClientLogCategory.Auth,
                wasReplaced
                    ? ReplacedMessage + error.ToDiagnostic()
                    : "The account session is no longer authorized. Clearing local session state and returning to LoginMenu. "
                        + error.ToDiagnostic());
            realtimeSession.Abort();
            SignOutToLogin();
            return true;
        }

        // The worker already closed the connection, so nothing is aborted.
        internal void ReturnToLoginAfterReplacement(RealtimeClientError error)
        {
            ClientLog.Error(ClientLogCategory.Auth, ReplacedMessage + error.ToDiagnostic());
            SignOutToLogin();
        }

        private void SignOutToLogin()
        {
            clientSession.SignOut();
            if (SceneManager.GetActiveScene().name != SceneNames.LoginMenu)
            {
                ClientSceneLoader.LoadLoginMenu();
            }
        }
    }
}
