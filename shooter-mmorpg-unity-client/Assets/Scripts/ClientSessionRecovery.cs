using Mmorpg.Client.Api;
using Mmorpg.Client.Diagnostics;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client
{
    public static class ClientSessionRecovery
    {
        public const string AccountSessionReplacedCode = "account_session_replaced";

        public static bool ReturnToLoginIfUnauthorized(ApiError error)
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
            if (ClientBootstrap.SimulationClient != null)
            {
                ClientBootstrap.SimulationClient.Abort();
            }

            ClientSession.Clear();
            if (SceneManager.GetActiveScene().name != SceneNames.LoginMenu)
            {
                SceneManager.LoadScene(SceneNames.LoginMenu);
            }

            return true;
        }
    }
}
