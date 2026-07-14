using ShooterMmo.Api;
using ShooterMmo.Diagnostics;
using UnityEngine.SceneManagement;

namespace ShooterMmo
{
    public static class ClientSessionRecovery
    {
        public const string AccountSessionReplacedCode = "account_session_replaced";

        public static bool ReturnToLoginIfUnauthorized(ShooterMmoApiError error)
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
            if (ShooterMmoClientBootstrap.SimulationClient != null)
            {
                ShooterMmoClientBootstrap.SimulationClient.Abort();
            }

            ShooterMmoClientSession.Clear();
            if (SceneManager.GetActiveScene().name != ShooterMmoSceneNames.LoginMenu)
            {
                SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
            }

            return true;
        }
    }
}
