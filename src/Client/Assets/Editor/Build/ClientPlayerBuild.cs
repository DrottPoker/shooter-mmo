using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Builds the Windows x64 player for one environment and build kind. tools/Build-Client.ps1
    // runs BuildFromCommandLine headless; the arguments name the environment file, the kind, the
    // output folder, and the stamp, which the script reads from Git:
    //
    //   -clientEnvironmentFile <path> -clientBuildKind development|release
    //   -clientBuildOutput <folder> -clientBuildCommit <id> -clientBuildDirty true|false
    //   -clientBuildTime <yyyy-MM-ddTHH:mm:ssZ> -clientBuildReport <file>
    //
    // It writes the build report as JSON and exits with 0 only when the player was built.
    public static class ClientPlayerBuild
    {
        public const string ExecutableName = "MMORPG.exe";

        public static void BuildFromCommandLine()
        {
            var exitCode = 1;
            try
            {
                var arguments = Environment.GetCommandLineArgs();
                var request = ParseRequest(arguments, out var output, out var reportPath);
                var summary = Build(request, output);
                File.WriteAllText(reportPath, JsonUtility.ToJson(summary, true) + "\n");
                exitCode = summary.result == BuildResult.Succeeded.ToString() ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogError("[BUILD] The client build failed: " + exception);
            }

            EditorApplication.Exit(exitCode);
        }

        // Builds the player into the output folder and returns its summary. The kind's player
        // settings and the baked build file last only for the build.
        public static ClientBuildSummary Build(ClientBuildInfo request, string outputFolder)
        {
            var kind = ClientBuildKindSettings.For(request.Stamp.Kind);
            var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(outputFolder, ExecutableName),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = kind.Options | BuildOptions.StrictMode
            };

            BuildReport report;
            ClientBuildFile.Request = request;
            try
            {
                using (kind.Apply())
                {
                    report = BuildPipeline.BuildPlayer(options);
                }
            }
            finally
            {
                ClientBuildFile.Request = null;
                ClientBuildFile.Delete();
            }

            return ClientBuildSummary.From(report, request, kind);
        }

        private static ClientBuildInfo ParseRequest(string[] arguments, out string output, out string reportPath)
        {
            var environmentFile = Argument(arguments, "-clientEnvironmentFile");
            var kindName = Argument(arguments, "-clientBuildKind");
            output = Path.GetFullPath(Argument(arguments, "-clientBuildOutput"));
            reportPath = Path.GetFullPath(Argument(arguments, "-clientBuildReport"));
            var expectedName = Path.GetFileNameWithoutExtension(environmentFile);
            if (!ClientEnvironment.TryParse(File.ReadAllText(environmentFile), expectedName, out var environment, out var error))
            {
                throw new ArgumentException("The environment " + environmentFile + " is invalid: " + error);
            }

            ClientBuildKind kind;
            if (kindName == ClientBuildInfo.DevelopmentKindName)
            {
                kind = ClientBuildKind.Development;
            }
            else if (kindName == ClientBuildInfo.ReleaseKindName)
            {
                kind = ClientBuildKind.Release;
            }
            else
            {
                throw new ArgumentException("The build kind '" + kindName + "' is neither development nor release.");
            }

            var time = Argument(arguments, "-clientBuildTime");
            if (!DateTime.TryParseExact(
                    time,
                    ClientBuildInfo.TimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out _))
            {
                throw new ArgumentException("The build time '" + time + "' is not " + ClientBuildInfo.TimeFormat + ".");
            }

            return new ClientBuildInfo(
                environment,
                new ClientBuildStamp(
                    kind,
                    Argument(arguments, "-clientBuildCommit"),
                    string.Equals(Argument(arguments, "-clientBuildDirty"), "true", StringComparison.OrdinalIgnoreCase),
                    time));
        }

        private static string Argument(IReadOnlyList<string> arguments, string name)
        {
            for (var index = 0; index + 1 < arguments.Count; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                {
                    return arguments[index + 1];
                }
            }

            throw new ArgumentException("The argument " + name + " is missing.");
        }
    }

    // What tools/Build-Client.ps1 prints and keeps beside the build, from the build report.
    [Serializable]
    public sealed class ClientBuildSummary
    {
        public string result;
        public string environment;
        public string gameServiceBaseUrl;
        public string kind;
        public string commit;
        public bool dirty;
        public string builtAtUtc;
        public string scriptingBackend;
        public string strippingLevel;
        public string il2cppCodeGeneration;
        public string il2cppCompilerConfiguration;
        public double buildSeconds;
        public ulong sizeBytes;
        public int errors;
        public int warnings;
        public string outputPath;
        public string[] errorMessages = Array.Empty<string>();
        public string[] warningMessages = Array.Empty<string>();

        public static ClientBuildSummary From(BuildReport report, ClientBuildInfo request, ClientBuildKindSettings kind)
        {
            var messages = report.steps.SelectMany(step => step.messages).ToArray();
            var errorMessages = messages
                .Where(message => message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Assert)
                .Select(message => message.content)
                .ToArray();
            var warningMessages = messages
                .Where(message => message.type == LogType.Warning)
                .Select(message => message.content)
                .ToArray();
            return new ClientBuildSummary
            {
                result = report.summary.result.ToString(),
                environment = request.Environment.Name,
                gameServiceBaseUrl = request.Environment.GameServiceBaseUrl,
                kind = ClientBuildInfo.KindName(request.Stamp.Kind),
                commit = request.Stamp.Commit,
                dirty = request.Stamp.Dirty,
                builtAtUtc = request.Stamp.BuiltAtUtc,
                scriptingBackend = kind.ScriptingBackend.ToString(),
                strippingLevel = kind.StrippingLevel.ToString(),
                il2cppCodeGeneration = kind.CodeGeneration.ToString(),
                il2cppCompilerConfiguration = kind.CompilerConfiguration.ToString(),
                buildSeconds = report.summary.totalTime.TotalSeconds,
                sizeBytes = report.summary.totalSize,
                errors = report.summary.totalErrors,
                warnings = report.summary.totalWarnings,
                outputPath = report.summary.outputPath,
                errorMessages = errorMessages,
                warningMessages = warningMessages
            };
        }
    }
}
