using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Mmorpg.Client.Editor
{
    internal sealed class DevelopmentToolProcessStartInfo
    {
        public DevelopmentToolProcessStartInfo(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Arguments = (arguments ?? throw new ArgumentNullException(nameof(arguments))).ToArray();
            WorkingDirectory = workingDirectory
                ?? throw new ArgumentNullException(nameof(workingDirectory));
            Environment = environment ?? new Dictionary<string, string>();
        }

        public string FileName { get; }

        public IReadOnlyList<string> Arguments { get; }

        public string WorkingDirectory { get; }

        public IReadOnlyDictionary<string, string> Environment { get; }
    }

    internal sealed class DevelopmentToolProcessResult
    {
        public DevelopmentToolProcessResult(
            int exitCode,
            bool timedOut,
            string standardOutput,
            string standardError)
        {
            ExitCode = exitCode;
            TimedOut = timedOut;
            StandardOutput = standardOutput ?? string.Empty;
            StandardError = standardError ?? string.Empty;
        }

        public int ExitCode { get; }

        public bool TimedOut { get; }

        public string StandardOutput { get; }

        public string StandardError { get; }
    }

    internal interface IDevelopmentToolProcess : IDisposable
    {
        // Returns false while the process runs. Reaching the timeout ends the process tree
        // and returns a timed out result.
        bool TryGetResult(out DevelopmentToolProcessResult result);
    }

    // One external process with captured output, polled from the Editor update loop.
    // A timeout or Dispose ends the process and every process it started.
    internal sealed class DevelopmentToolProcess : IDevelopmentToolProcess
    {
        // A descendant that inherited the output pipes can hold them open after the root
        // exited. The result is then read without waiting longer for the pipes.
        private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);
        private const int TaskKillTimeoutMilliseconds = 10000;

        private readonly object outputLock = new object();
        private readonly List<string> standardOutput = new List<string>();
        private readonly List<string> standardError = new List<string>();
        private readonly Process process;
        private readonly TimeSpan timeout;
        private readonly Stopwatch runtime = Stopwatch.StartNew();
        private Stopwatch exitedFor;
        private bool standardOutputClosed;
        private bool standardErrorClosed;
        private bool disposed;

        private DevelopmentToolProcess(Process process, TimeSpan timeout)
        {
            this.process = process;
            this.timeout = timeout;
            process.OutputDataReceived += CaptureStandardOutput;
            process.ErrorDataReceived += CaptureStandardError;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        // True once both output pipes reached end of file, which means no process in the
        // tree still holds them.
        internal bool OutputClosed
        {
            get
            {
                lock (outputLock)
                {
                    return standardOutputClosed && standardErrorClosed;
                }
            }
        }

        public static DevelopmentToolProcess Start(
            DevelopmentToolProcessStartInfo startInfo,
            TimeSpan timeout)
        {
            if (startInfo == null)
            {
                throw new ArgumentNullException(nameof(startInfo));
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = startInfo.FileName,
                Arguments = string.Join(" ", startInfo.Arguments.Select(QuoteArgument)),
                WorkingDirectory = startInfo.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var variable in startInfo.Environment)
            {
                processStartInfo.EnvironmentVariables[variable.Key] = variable.Value;
            }

            var process = new Process { StartInfo = processStartInfo };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(startInfo.FileName + " did not start.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            return new DevelopmentToolProcess(process, timeout);
        }

        public bool TryGetResult(out DevelopmentToolProcessResult result)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(DevelopmentToolProcess));
            }

            result = null;
            if (HasExited())
            {
                exitedFor = exitedFor ?? Stopwatch.StartNew();
                if (!OutputClosed && exitedFor.Elapsed < OutputDrainTimeout)
                {
                    return false;
                }

                result = CreateResult(process.ExitCode, false);
                return true;
            }

            if (runtime.Elapsed <= timeout)
            {
                return false;
            }

            EndProcessTree();
            result = CreateResult(-1, true);
            return true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            EndProcessTree();
            process.OutputDataReceived -= CaptureStandardOutput;
            process.ErrorDataReceived -= CaptureStandardError;
            process.Dispose();
        }

        // Quotes one argument for the Windows command line parser, which Mono and .NET
        // also apply on other platforms.
        internal static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.All(character => !char.IsWhiteSpace(character) && character != '"'))
            {
                return value;
            }

            var builder = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                // Backslashes are literal unless a quote follows them.
                builder.Append('\\', character == '"' ? (backslashes * 2) + 1 : backslashes);
                builder.Append(character);
                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private DevelopmentToolProcessResult CreateResult(int exitCode, bool timedOut)
        {
            lock (outputLock)
            {
                return new DevelopmentToolProcessResult(
                    exitCode,
                    timedOut,
                    string.Join(System.Environment.NewLine, standardOutput),
                    string.Join(System.Environment.NewLine, standardError));
            }
        }

        private void EndProcessTree()
        {
            if (HasExited())
            {
                return;
            }

            // Process.Kill in this runtime ends only the root, so Windows uses taskkill to
            // end every descendant too. Other platforms end the root only.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RunTaskKill(process.Id);
            }

            if (HasExited())
            {
                return;
            }

            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and the kill.
            }
            catch (Win32Exception)
            {
                // The process is already terminating.
            }
        }

        private static void RunTaskKill(int processId)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(System.Environment.SystemDirectory, "taskkill.exe"),
                Arguments = "/T /F /PID " + processId.ToString(CultureInfo.InvariantCulture),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try
            {
                using (var taskKill = new Process { StartInfo = startInfo })
                {
                    taskKill.OutputDataReceived += IgnoreOutput;
                    taskKill.ErrorDataReceived += IgnoreOutput;
                    taskKill.Start();
                    taskKill.BeginOutputReadLine();
                    taskKill.BeginErrorReadLine();
                    taskKill.WaitForExit(TaskKillTimeoutMilliseconds);
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception || exception is InvalidOperationException)
            {
                // The caller falls back to ending the root process.
            }
        }

        private bool HasExited()
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        private void CaptureStandardOutput(object sender, DataReceivedEventArgs eventArgs)
        {
            lock (outputLock)
            {
                if (eventArgs.Data == null)
                {
                    standardOutputClosed = true;
                }
                else
                {
                    standardOutput.Add(eventArgs.Data);
                }
            }
        }

        private void CaptureStandardError(object sender, DataReceivedEventArgs eventArgs)
        {
            lock (outputLock)
            {
                if (eventArgs.Data == null)
                {
                    standardErrorClosed = true;
                }
                else
                {
                    standardError.Add(eventArgs.Data);
                }
            }
        }

        private static void IgnoreOutput(object sender, DataReceivedEventArgs eventArgs)
        {
        }
    }
}
