using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Mmorpg.WorldData.Editor
{
    public sealed class ContentCompilerRunResult
    {
        public ContentCompilerRunResult(bool success, string output)
        {
            Success = success;
            Output = output ?? string.Empty;
        }

        public bool Success { get; }

        public string Output { get; }
    }

    // Compiles a content root into a generated root, or verifies that they match.
    public interface IContentCompilerRunner
    {
        ContentCompilerRunResult Compile(string contentRoot, string generatedRoot);

        ContentCompilerRunResult Verify(string contentRoot, string generatedRoot);
    }

    // Runs a content compiler from tools/ through dotnet run, the entry point CI uses, so
    // the Editor never accepts content that the strict command line compiler rejects.
    public sealed class ContentCompilerProcessRunner : IContentCompilerRunner
    {
        private const int TimeoutMilliseconds = 120000;
        private readonly string repositoryRoot;
        private readonly string toolName;
        private readonly string projectPath;

        public ContentCompilerProcessRunner(string repositoryRoot, string toolName)
        {
            this.repositoryRoot = Path.GetFullPath(
                repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
            this.toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            projectPath = RepositoryLayout.ToolProjectPath(this.repositoryRoot, toolName);
        }

        public ContentCompilerRunResult Compile(string contentRoot, string generatedRoot)
        {
            return Run(contentRoot, generatedRoot, false);
        }

        public ContentCompilerRunResult Verify(string contentRoot, string generatedRoot)
        {
            return Run(contentRoot, generatedRoot, true);
        }

        private ContentCompilerRunResult Run(
            string contentRoot,
            string generatedRoot,
            bool verifyOnly)
        {
            if (!File.Exists(projectPath))
            {
                return new ContentCompilerRunResult(
                    false,
                    toolName + " project was not found at '" + projectPath + "'.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --project " + Quote(projectPath)
                    + " --configuration Release --"
                    + " --content-root " + Quote(Path.GetFullPath(contentRoot))
                    + " --generated-root " + Quote(Path.GetFullPath(generatedRoot))
                    + (verifyOnly ? " --verify" : string.Empty),
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return new ContentCompilerRunResult(
                            false,
                            "Could not start " + toolName + ".");
                    }

                    var standardOutput = process.StandardOutput.ReadToEndAsync();
                    var standardError = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        TryKill(process);
                        return new ContentCompilerRunResult(
                            false,
                            toolName + " exceeded the two-minute timeout.");
                    }

                    Task.WaitAll(new Task[] { standardOutput, standardError });
                    var output = CombineOutput(standardOutput.Result, standardError.Result);
                    return new ContentCompilerRunResult(process.ExitCode == 0, output);
                }
            }
            catch (Exception exception)
            {
                return new ContentCompilerRunResult(
                    false,
                    "Could not run " + toolName + ": " + exception.Message);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string CombineOutput(string standardOutput, string standardError)
        {
            var output = (standardOutput ?? string.Empty).Trim();
            var error = (standardError ?? string.Empty).Trim();
            if (output.Length == 0)
            {
                return error;
            }

            return error.Length == 0 ? output : output + Environment.NewLine + error;
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // The process may have exited between the timeout and cleanup.
            }
        }
    }
}
