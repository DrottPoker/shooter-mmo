using ShooterMmo.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShooterMmo
{
    public static class ClientSessionRecovery
    {
        public static bool ReturnToLoginIfUnauthorized(ShooterMmoApiError error)
        {
            if (error == null || !error.IsUnauthorized)
            {
                return false;
            }

            Debug.LogWarning("Session is no longer authorized. Returning to LoginMenu.");
            ShooterMmoClientSession.Clear();
            if (SceneManager.GetActiveScene().name != ShooterMmoSceneNames.LoginMenu)
            {
                SceneManager.LoadScene(ShooterMmoSceneNames.LoginMenu);
            }

            return true;
        }
    }
}
