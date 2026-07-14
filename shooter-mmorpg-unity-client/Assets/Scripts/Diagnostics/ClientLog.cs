using UnityEngine;

namespace ShooterMmo.Diagnostics
{
    public enum ClientLogCategory
    {
        Auth,
        Client,
        Simulation
    }

    public static class ClientLog
    {
        public static void Info(ClientLogCategory category, string message)
        {
            Debug.Log(Format(category, message));
        }

        public static void Warning(ClientLogCategory category, string message)
        {
            Debug.LogWarning(Format(category, message));
        }

        public static void Error(ClientLogCategory category, string message)
        {
            Debug.LogError(Format(category, message));
        }

        internal static string Format(ClientLogCategory category, string message)
        {
            var normalizedMessage = string.IsNullOrWhiteSpace(message)
                ? "No details were provided."
                : message.Trim().Replace('\r', ' ').Replace('\n', ' ');

            return "[" + category.ToString().ToUpperInvariant() + "] " + normalizedMessage;
        }
    }
}
