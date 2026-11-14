using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Mmorpg.Client.Core
{
    // What kind of client runs: the Editor, a development build, or a release build.
    public enum ClientBuildKind
    {
        Editor,
        Development,
        Release
    }

    // Which build this is: its kind, the commit it was built from, whether the working tree had
    // changes the commit does not hold, and when it was built. The Editor has no commit and time.
    public sealed class ClientBuildStamp
    {
        public static readonly ClientBuildStamp Editor =
            new ClientBuildStamp(ClientBuildKind.Editor, string.Empty, false, string.Empty);

        public ClientBuildStamp(ClientBuildKind kind, string commit, bool dirty, string builtAtUtc)
        {
            Kind = kind;
            Commit = commit ?? string.Empty;
            Dirty = dirty;
            BuiltAtUtc = builtAtUtc ?? string.Empty;
        }

        public ClientBuildKind Kind { get; }

        // The full commit id, empty when it is unknown.
        public string Commit { get; }

        public bool Dirty { get; }

        // ISO 8601 in UTC, such as 2026-11-14T09:30:00Z, empty in the Editor.
        public string BuiltAtUtc { get; }

        // The Editor and development builds have the development tools, such as the Admin menu and
        // the override of the GameService address; a release build has none of them.
        public bool HasDevelopmentTools
        {
            get { return Kind != ClientBuildKind.Release; }
        }

        // The first ten characters of the commit, or "unknown".
        public string ShortCommit
        {
            get { return Commit.Length == 0 ? "unknown" : Commit.Substring(0, Math.Min(10, Commit.Length)); }
        }

        // The build time as yyyy-MM-dd HH:mm, or the stored text when it cannot be read.
        public string BuiltAtDisplay
        {
            get
            {
                return DateTime.TryParseExact(
                        BuiltAtUtc,
                        ClientBuildInfo.TimeFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var time)
                    ? time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : BuiltAtUtc;
            }
        }

        // One line for logs and the Debug menu, such as "development build 0a1b2c3d4e (uncommitted
        // changes), built 2026-11-14 09:30 UTC".
        public string Describe()
        {
            if (Kind == ClientBuildKind.Editor)
            {
                return "Unity Editor";
            }

            return ClientBuildInfo.KindName(Kind) + " build " + ShortCommit
                + (Dirty ? " (uncommitted changes)" : string.Empty)
                + ", built " + BuiltAtDisplay + " UTC";
        }
    }

    // The environment and the build stamp of this client. The Editor reads the local environment
    // from src/Client/Environments/local.json and has the Editor stamp. A player build bakes one
    // environment and its stamp into the client content as client-build.json, which the
    // Editor's build step writes for the build and deletes afterwards, so it never exists in
    // the project otherwise; a player reads that file and nothing else, so only a change of the
    // game files points a release build elsewhere.
    public sealed class ClientBuildInfo
    {
        public const int FormatVersion = 1;
        public const string TimeFormat = "yyyy-MM-ddTHH:mm:ssZ";
        public const string DevelopmentKindName = "development";
        public const string ReleaseKindName = "release";

        // The GameService address of a client that has no valid environment: a name that never
        // resolves, so every request fails with an error that names it.
        public const string MissingEnvironmentAddress = "http://environment-missing.invalid";

        public ClientBuildInfo(ClientEnvironment environment, ClientBuildStamp stamp)
        {
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            Stamp = stamp ?? throw new ArgumentNullException(nameof(stamp));
        }

        public ClientEnvironment Environment { get; }

        public ClientBuildStamp Stamp { get; }

        // The environment and stamp of the running client. The Editor's error and a player's are
        // logged, and the client then runs with an environment that reaches no GameService.
        public static ClientBuildInfo Load()
        {
#if UNITY_EDITOR
            var path = EditorEnvironmentPath(Application.dataPath);
            string json = null;
            var error = string.Empty;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                error = exception.Message;
            }

            if (json != null
                && ClientEnvironment.TryParse(json, ClientEnvironment.LocalName, out var environment, out error))
            {
                return new ClientBuildInfo(environment, ClientBuildStamp.Editor);
            }

            Debug.LogError("The Editor's environment " + path + " cannot be read: " + error);
            return Missing(ClientBuildStamp.Editor);
#else
            if (ClientAssets.TryReadText(ClientAddresses.ClientBuild, out var json, out var error)
                && TryParse(json, out var info, out error))
            {
                return info;
            }

            Debug.LogError("This build has no valid environment: " + error);
            return Missing(new ClientBuildStamp(
                Debug.isDebugBuild ? ClientBuildKind.Development : ClientBuildKind.Release,
                string.Empty,
                false,
                string.Empty));
#endif
        }

        // The local environment file of the Unity project whose Assets folder is given.
        public static string EditorEnvironmentPath(string assetsFolder)
        {
            return Path.GetFullPath(Path.Combine(
                assetsFolder,
                "..",
                ClientEnvironment.FolderName,
                ClientEnvironment.LocalName + ".json"));
        }

        public static string KindName(ClientBuildKind kind)
        {
            switch (kind)
            {
                case ClientBuildKind.Development:
                    return DevelopmentKindName;
                case ClientBuildKind.Release:
                    return ReleaseKindName;
                default:
                    return "editor";
            }
        }

        // Reads a baked build file. Only a development or release stamp can be baked.
        public static bool TryParse(string json, out ClientBuildInfo info, out string error)
        {
            info = null;
            ClientBuildDocument document;
            try
            {
                document = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<ClientBuildDocument>(json);
            }
            catch (ArgumentException exception)
            {
                error = "The build file is no JSON object: " + exception.Message;
                return false;
            }

            if (document == null)
            {
                error = "The build file is empty.";
                return false;
            }

            if (document.formatVersion != FormatVersion)
            {
                error = "The build file has format " + document.formatVersion + ", this client reads " + FormatVersion + ".";
                return false;
            }

            if (!ClientEnvironment.TryCreate(document.environment, null, out var environment, out error))
            {
                return false;
            }

            ClientBuildKind kind;
            if (string.Equals(document.kind, DevelopmentKindName, StringComparison.Ordinal))
            {
                kind = ClientBuildKind.Development;
            }
            else if (string.Equals(document.kind, ReleaseKindName, StringComparison.Ordinal))
            {
                kind = ClientBuildKind.Release;
            }
            else
            {
                error = "The build kind '" + document.kind + "' is neither development nor release.";
                return false;
            }

            if (!DateTime.TryParseExact(
                    document.builtAtUtc,
                    TimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out _))
            {
                error = "The build time '" + document.builtAtUtc + "' is not " + TimeFormat + ".";
                return false;
            }

            info = new ClientBuildInfo(
                environment,
                new ClientBuildStamp(kind, document.commit, document.dirty, document.builtAtUtc));
            error = string.Empty;
            return true;
        }

        // The baked build file, which the Editor's build step writes. The Editor stamp has none.
        public string ToJson()
        {
            if (Stamp.Kind == ClientBuildKind.Editor)
            {
                throw new InvalidOperationException("Only a development or release build is baked.");
            }

            return JsonUtility.ToJson(
                new ClientBuildDocument
                {
                    formatVersion = FormatVersion,
                    environment = Environment.ToDocument(),
                    kind = KindName(Stamp.Kind),
                    commit = Stamp.Commit,
                    dirty = Stamp.Dirty,
                    builtAtUtc = Stamp.BuiltAtUtc
                },
                true);
        }

        private static ClientBuildInfo Missing(ClientBuildStamp stamp)
        {
            return new ClientBuildInfo(new ClientEnvironment("missing", MissingEnvironmentAddress), stamp);
        }
    }

    [Serializable]
    internal sealed class ClientBuildDocument
    {
        public int formatVersion;
        public ClientEnvironmentDocument environment;
        public string kind;
        public string commit;
        public bool dirty;
        public string builtAtUtc;
    }
}
