using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // Where a client connects: one file of src/Client/Environments, such as local.json. The
    // Editor always runs the local environment from that file; a player runs the one environment
    // that its build baked in, as ClientBuildInfo describes.
    public sealed class ClientEnvironment
    {
        // The environment of the Editor and of every player build that names no other.
        public const string LocalName = "local";

        // Below the Unity project folder, src/Client.
        public const string FolderName = "Environments";

        private static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9-]{0,63}$");

        public ClientEnvironment(string name, string gameServiceBaseUrl)
        {
            if (!IsValidName(name))
            {
                throw new ArgumentException(
                    "An environment name is lowercase letters, digits, and hyphens.",
                    nameof(name));
            }

            if (!TryNormalizeAddress(gameServiceBaseUrl, out var address))
            {
                throw new ArgumentException(
                    "A GameService base URL is an absolute http or https URL.",
                    nameof(gameServiceBaseUrl));
            }

            Name = name;
            GameServiceBaseUrl = address;
        }

        public string Name { get; }

        // Without a trailing slash.
        public string GameServiceBaseUrl { get; }

        public static bool IsValidName(string name)
        {
            return !string.IsNullOrEmpty(name) && NamePattern.IsMatch(name);
        }

        // An absolute http or https URL with a host and without user information, a query, or a
        // fragment, returned without its trailing slash.
        public static bool TryNormalizeAddress(string value, out string address)
        {
            address = string.Empty;
            if (string.IsNullOrWhiteSpace(value)
                || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || uri.UserInfo.Length > 0
                || uri.Query.Length > 0
                || uri.Fragment.Length > 0)
            {
                return false;
            }

            address = value.Trim().TrimEnd('/');
            return true;
        }

        // Reads an environment file. A file must name the environment it is, so a renamed copy
        // cannot stand in for another environment.
        public static bool TryParse(string json, string expectedName, out ClientEnvironment environment, out string error)
        {
            environment = null;
            ClientEnvironmentDocument document;
            try
            {
                document = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<ClientEnvironmentDocument>(json);
            }
            catch (ArgumentException exception)
            {
                error = "The environment is no JSON object: " + exception.Message;
                return false;
            }

            return TryCreate(document, expectedName, out environment, out error);
        }

        internal static bool TryCreate(
            ClientEnvironmentDocument document,
            string expectedName,
            out ClientEnvironment environment,
            out string error)
        {
            environment = null;
            if (document == null)
            {
                error = "The environment is empty.";
                return false;
            }

            if (!IsValidName(document.name))
            {
                error = "The environment name '" + document.name + "' is not lowercase letters, digits, and hyphens.";
                return false;
            }

            if (expectedName != null && !string.Equals(document.name, expectedName, StringComparison.Ordinal))
            {
                error = "The environment file of '" + expectedName + "' names the environment '" + document.name + "'.";
                return false;
            }

            if (!TryNormalizeAddress(document.gameServiceBaseUrl, out _))
            {
                error = "The environment '" + document.name + "' has no absolute http or https gameServiceBaseUrl.";
                return false;
            }

            environment = new ClientEnvironment(document.name, document.gameServiceBaseUrl);
            error = string.Empty;
            return true;
        }

        internal ClientEnvironmentDocument ToDocument()
        {
            return new ClientEnvironmentDocument
            {
                name = Name,
                gameServiceBaseUrl = GameServiceBaseUrl
            };
        }
    }

    // The JSON form of an environment file and of the environment inside a baked build file.
    [Serializable]
    internal sealed class ClientEnvironmentDocument
    {
        public string name;
        public string gameServiceBaseUrl;
    }
}
