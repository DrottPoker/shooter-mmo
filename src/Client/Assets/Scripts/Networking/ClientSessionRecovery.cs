using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Worlds;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Networking
{
    public static class ClientSessionRecovery
    {
        public const string AccountSessionReplacedCode = "account_session_replaced";

        // The caller passes the persistent realtime client it was given, or null when it has none.
        public static bool ReturnToLoginIfUnauthorized(
            ApiError error,
            RealtimeSimulationClient simulationClient)
        {
            if (error == null || !error.IsUnauthorized)
            {
                return false;
            }

            var wasReplaced = string.Equals(
                error.Code,
                AccountSessionReplacedCode,
                System.StringComparison.Ordinal);
            ClientLog.Error(
                ClientLogCategory.Auth,
                wasReplaced
                    ? "This client was disconnected because the account logged in from another client. "
                        + error.ToDisplayMessage()
                    : "The account session is no longer authorized. Clearing local session state and returning to LoginMenu. "
                        + error.ToDisplayMessage());
            if (simulationClient != null)
            {
                simulationClient.Abort();
            }

            ClientSession.Clear();
            if (SceneManager.GetActiveScene().name != SceneNames.LoginMenu)
            {
                ClientSceneLoader.LoadLoginMenu();
            }

            return true;
        }
    }
}
