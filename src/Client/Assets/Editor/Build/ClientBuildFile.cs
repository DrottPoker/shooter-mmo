using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Mmorpg.Client.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Mmorpg.Client.Editor
{
    // Bakes the environment and the build stamp into every player build as the client content
    // file client-build.json, which ClientBuildInfo reads in the player. It runs before the
    // Addressables content build, which builds the file into the Client Content bundle, and
    // deletes the file after the build, so the file exists only while a build runs; Git ignores
    // it. A build of tools/Build-Client.ps1 names its environment and stamp through Request; any
    // other build, from the Build Profiles window or a test player, bakes the local environment
    // with the commit of the checkout.
    public sealed class ClientBuildFile : BuildPlayerProcessor, IPostprocessBuildWithReport
    {
        public const string AssetPath = "Assets/Content/" + ClientAddresses.ClientBuild;

        // Before the Addressables player build processor, whose order is 1.
        public override int callbackOrder
        {
            get { return 0; }
        }

        // The build info of the next player build, set by ClientPlayerBuild for its build and
        // cleared after it. Editor state of one build, never read by the client.
        public static ClientBuildInfo Request { get; set; }

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            var development = (buildPlayerContext.BuildPlayerOptions.options & BuildOptions.Development) != 0;
            var info = Request ?? DefaultBuild(development ? ClientBuildKind.Development : ClientBuildKind.Release);
            if (info.Stamp.Kind != (development ? ClientBuildKind.Development : ClientBuildKind.Release))
            {
                throw new BuildFailedException(
                    "The requested " + ClientBuildInfo.KindName(info.Stamp.Kind) + " build does not match the build options.");
            }

            Write(info);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Delete();
        }

        // The local environment with the commit and state of the checkout, now.
        public static ClientBuildInfo DefaultBuild(ClientBuildKind kind)
        {
            var path = ClientBuildInfo.EditorEnvironmentPath(Application.dataPath);
            if (!ClientEnvironment.TryParse(File.ReadAllText(path), ClientEnvironment.LocalName, out var environment, out var error))
            {
                throw new BuildFailedException("The local environment " + path + " is invalid: " + error);
            }

            var commit = Git("rev-parse HEAD");
            var status = Git("status --porcelain");
            return new ClientBuildInfo(
                environment,
                new ClientBuildStamp(
                    kind,
                    commit ?? string.Empty,
                    !string.IsNullOrEmpty(status),
                    DateTime.UtcNow.ToString(ClientBuildInfo.TimeFormat, CultureInfo.InvariantCulture)));
        }

        public static void Write(ClientBuildInfo info)
        {
            File.WriteAllText(AssetPath, info.ToJson() + "\n");
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[BUILD] Baked " + info.Environment.Name + " (" + info.Environment.GameServiceBaseUrl + "), "
                + info.Stamp.Describe() + ".");
        }

        public static void Delete()
        {
            if (File.Exists(AssetPath) || File.Exists(AssetPath + ".meta"))
            {
                if (!AssetDatabase.DeleteAsset(AssetPath))
                {
                    File.Delete(AssetPath);
                    File.Delete(AssetPath + ".meta");
                }
            }
        }

        // The trimmed output of a Git command in the project, or null when Git is unavailable.
        private static string Git(string arguments)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("git", arguments)
                       {
                           UseShellExecute = false,
                           RedirectStandardOutput = true,
                           RedirectStandardError = true,
                           CreateNoWindow = true,
                           WorkingDirectory = Path.GetDirectoryName(Application.dataPath) ?? "."
                       }))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 ? output.Trim() : null;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
