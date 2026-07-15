using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ShooterMmo.WorldData.Editor.Items
{
    public sealed class ItemCatalogCompilerRunResult
    {
        public ItemCatalogCompilerRunResult(bool success, string output)
        {
            Success = success;
            Output = output ?? string.Empty;
        }

        public bool Success { get; }

        public string Output { get; }
    }

    public interface IItemCatalogCompilerRunner
    {
        ItemCatalogCompilerRunResult Compile(string authoringPath, string runtimePath);
    }

    public sealed class ItemCatalogCompilerProcessRunner : IItemCatalogCompilerRunner
    {
        private const int TimeoutMilliseconds = 120000;
        private readonly string repositoryRoot;
        private readonly string compilerProjectPath;

        public ItemCatalogCompilerProcessRunner(
            string repositoryRoot,
            string compilerProjectPath)
        {
            this.repositoryRoot = Path.GetFullPath(
                repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
            this.compilerProjectPath = Path.GetFullPath(
                compilerProjectPath ?? throw new ArgumentNullException(nameof(compilerProjectPath)));
        }

        public ItemCatalogCompilerRunResult Compile(
            string authoringPath,
            string runtimePath)
        {
            return Run(authoringPath, runtimePath, false);
        }

        public ItemCatalogCompilerRunResult Verify(
            string authoringPath,
            string runtimePath)
        {
            return Run(authoringPath, runtimePath, true);
        }

        private ItemCatalogCompilerRunResult Run(
            string authoringPath,
            string runtimePath,
            bool verifyOnly)
        {
            if (!File.Exists(compilerProjectPath))
            {
                return new ItemCatalogCompilerRunResult(
                    false,
                    "ItemCatalogCompiler project was not found at '"
                        + compilerProjectPath + "'.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --project " + Quote(compilerProjectPath)
                    + " --configuration Release -- "
                    + Quote(Path.GetFullPath(authoringPath)) + " "
                    + Quote(Path.GetFullPath(runtimePath))
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
                        return new ItemCatalogCompilerRunResult(
                            false,
                            "Could not start the dotnet item catalog compiler.");
                    }

                    var standardOutput = process.StandardOutput.ReadToEndAsync();
                    var standardError = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        TryKill(process);
                        return new ItemCatalogCompilerRunResult(
                            false,
                            "Item catalog compilation exceeded the two-minute timeout.");
                    }

                    Task.WaitAll(new Task[] { standardOutput, standardError });
                    var output = CombineOutput(standardOutput.Result, standardError.Result);
                    return new ItemCatalogCompilerRunResult(process.ExitCode == 0, output);
                }
            }
            catch (Exception exception)
            {
                return new ItemCatalogCompilerRunResult(
                    false,
                    "Could not run the dotnet item catalog compiler: " + exception.Message);
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

            if (error.Length == 0)
            {
                return output;
            }

            return output + Environment.NewLine + error;
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
