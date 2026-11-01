using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using Mmorpg.WorldData.Editor;
using Mmorpg.WorldData.Editor.Items;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class DevelopmentEditorToolsTests
    {
        private const string EmptyListResponseLine = "MMORPG_DEV_ITEMS_JSON:{\"success\":true,"
            + "\"message\":\"Loaded.\",\"data\":{\"characters\":[],\"definitions\":[],\"packages\":[]}}";

        private const string ResultOutputFileSwitch = "-getResultOutputFile:";

        private static readonly string RepositoryRoot = Path.Combine(Path.GetTempPath(), "mmorpg-repository");

        [Test]
        public void EveryEditorToolUsesTheSharedMenuRoot()
        {
            Assert.That(
                InventoryItemGrantWindow.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                WorldCollisionBaker.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                WorldCollisionBaker.BakeWorldScenesMenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                ItemCatalogEditorWindow.MenuPath,
                Does.StartWith("MMORPG/Tools/"));
            Assert.That(
                ClientAddressablesLayout.ApplyMenuPath,
                Does.StartWith("MMORPG/Tools/"));
        }

        [Test]
        public void WorldBakeOpensASharedSceneOnceForEveryWorld()
        {
            var plans = WorldCollisionBaker.PlanWorldBakes(
                new[]
                {
                    new WorldSceneCatalogEntry { worldId = "world-a", sceneName = "SharedArena" },
                    new WorldSceneCatalogEntry { worldId = "world-b", sceneName = "SharedArena" },
                    new WorldSceneCatalogEntry { worldId = "world-c", sceneName = "Solo" }
                },
                new[] { "Assets/Scenes/SharedArena.unity", "Assets/Scenes/Solo.unity" });

            Assert.That(plans.Count, Is.EqualTo(2));
            Assert.That(plans[0].ScenePath, Is.EqualTo("Assets/Scenes/SharedArena.unity"));
            Assert.That(plans[0].WorldIds, Is.EqualTo(new[] { "world-a", "world-b" }));
            Assert.That(plans[1].WorldIds, Is.EqualTo(new[] { "world-c" }));
            Assert.Throws<InvalidOperationException>(() => WorldCollisionBaker.PlanWorldBakes(
                new[] { new WorldSceneCatalogEntry { worldId = "world-d", sceneName = "Missing" } },
                new[] { "Assets/Scenes/Solo.unity" }));
        }

        [Test]
        public void ItemToolProtocolParsesResponseAmongDotnetLogs()
        {
            const string output = "info: startup\n"
                + "MMORPG_DEV_ITEMS_JSON:{\"success\":true,\"message\":\"Loaded.\","
                + "\"data\":{\"characters\":[{\"characterId\":\"character-1\","
                + "\"characterName\":\"Hero\",\"accountUsername\":\"player\","
                + "\"isOnline\":false,\"itemCount\":0,\"recoveryDeliveryCount\":0,"
                + "\"itemStateRevision\":0,\"carriedWeight\":0,\"carryCapacity\":200}],"
                + "\"definitions\":[{\"definitionId\":\"material.iron_ore\","
                + "\"displayName\":\"Iron Ore\",\"categoryId\":\"material\","
                + "\"maximumStackSize\":100,\"unitWeight\":6}],"
                + "\"packages\":[{\"packageId\":\"encumbrance_140\","
                + "\"displayName\":\"Encumbrance 140% Pack\","
                + "\"description\":\"Hard cap.\",\"requiresEmptyCharacter\":true}]}}";

            var parsed = DevelopmentItemToolCli.TryParseResponse(
                output,
                out var response,
                out var error);

            Assert.That(parsed, Is.True, error);
            Assert.That(response.success, Is.True);
            Assert.That(response.data.characters[0].carryCapacity, Is.EqualTo(200));
            Assert.That(
                response.data.definitions[0].maximumStackSize,
                Is.EqualTo(100));
            Assert.That(
                response.data.packages[0].requiresEmptyCharacter,
                Is.True);
        }

        [Test]
        public void ItemToolProtocolRejectsMissingMachineReadableResponse()
        {
            var parsed = DevelopmentItemToolCli.TryParseResponse(
                "dotnet failed before GameService started",
                out var response,
                out var error);

            Assert.That(parsed, Is.False);
            Assert.That(response, Is.Null);
            Assert.That(error, Does.Contain("did not return a development item response"));
        }

        [Test]
        public void ItemToolBuildsGameServiceInReleaseWithoutBuildServers()
        {
            var startInfo = DevelopmentItemToolCli.CreateBuildStartInfo(
                RepositoryRoot,
                "target-path.txt");

            // -getProperty without -t:Build only evaluates, so the exact list is pinned.
            Assert.That(startInfo.FileName, Is.EqualTo("dotnet"));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo(RepositoryRoot));
            Assert.That(
                startInfo.Arguments,
                Is.EqualTo(new[]
                {
                    "build",
                    RepositoryLayout.GameServiceProjectPath(RepositoryRoot),
                    "--configuration",
                    "Release",
                    "--disable-build-servers",
                    "-m:1",
                    "-t:Build",
                    "-getProperty:TargetPath",
                    "-getResultOutputFile:target-path.txt",
                    "-nologo",
                    "-tl:off",
                    "-v:q"
                }));
        }

        [Test]
        public void ItemToolRunsExactlyTheAssemblyThatTheBuildReported()
        {
            var processes = new FakeToolProcesses();
            var assemblyPath = Path.GetTempFileName();
            try
            {
                using (var operation = DevelopmentItemToolCliProcess.Start(
                    RepositoryRoot,
                    new[] { "--dev-items-list" },
                    processes.Start))
                {
                    Assert.That(operation.IsBuilding, Is.True);
                    Assert.That(operation.TryComplete(out _, out _), Is.False);
                    var build = processes.Started.Single();
                    Assert.That(build.StartInfo.Arguments[0], Is.EqualTo("build"));
                    Assert.That(build.Timeout, Is.EqualTo(DevelopmentItemToolCliProcess.BuildTimeout));

                    CompleteBuild(build, assemblyPath);
                    Assert.That(operation.TryComplete(out _, out var error), Is.False, error);
                    Assert.That(operation.IsBuilding, Is.False);
                    Assert.That(build.Disposed, Is.True);
                    var command = processes.Started[1];
                    Assert.That(command.StartInfo.FileName, Is.EqualTo("dotnet"));
                    Assert.That(
                        command.StartInfo.Arguments,
                        Is.EqualTo(new[] { assemblyPath, "--dev-items-list" }));
                    Assert.That(
                        command.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"],
                        Is.EqualTo("Development"));
                    Assert.That(command.Timeout, Is.EqualTo(DevelopmentItemToolCliProcess.CommandTimeout));

                    command.Result = new DevelopmentToolProcessResult(
                        0,
                        false,
                        "info: startup\n" + EmptyListResponseLine,
                        string.Empty);
                    Assert.That(operation.TryComplete(out var response, out error), Is.True);
                    Assert.That(response, Is.Not.Null, error);
                    Assert.That(response.success, Is.True);
                    Assert.That(command.Disposed, Is.True);
                }
            }
            finally
            {
                File.Delete(assemblyPath);
            }
        }

        [Test]
        public void ItemToolNeverRunsGameServiceWhenTheBuildFailsOrTimesOut()
        {
            const string compilerError = @"D:\repo\src\Server\GameService.Items\ItemsCommandLine.cs(25,28): "
                + "error CS0103: The name 'undefinedName' does not exist in the current context";
            const string projectSuffix = @" [D:\repo\src\Server\GameService.Items\Mmorpg.GameService.Items.csproj]";
            var failedProcesses = new FakeToolProcesses();
            using (var operation = DevelopmentItemToolCliProcess.Start(
                RepositoryRoot,
                new[] { "--dev-items-list" },
                failedProcesses.Start))
            {
                failedProcesses.Started.Single().Result = new DevelopmentToolProcessResult(
                    1,
                    false,
                    compilerError + projectSuffix + "\n\nBuild FAILED.\n\n"
                        + compilerError + projectSuffix + "\n    0 Warning(s)\n    1 Error(s)",
                    string.Empty);

                Assert.That(operation.TryComplete(out var response, out var error), Is.True);
                Assert.That(response, Is.Null);
                Assert.That(failedProcesses.Started.Count, Is.EqualTo(1));
                Assert.That(
                    error,
                    Is.EqualTo("GameService could not be built, so the tool did not run an older build. "
                        + "dotnet build exited with code 1.\n" + compilerError));
                Assert.That(operation.FailureOutput, Does.Contain("Build FAILED."));
            }

            var timedOutProcesses = new FakeToolProcesses();
            using (var operation = DevelopmentItemToolCliProcess.Start(
                RepositoryRoot,
                new[] { "--dev-items-list" },
                timedOutProcesses.Start))
            {
                timedOutProcesses.Started.Single().Result = new DevelopmentToolProcessResult(
                    -1,
                    true,
                    string.Empty,
                    string.Empty);

                Assert.That(operation.TryComplete(out var response, out var error), Is.True);
                Assert.That(response, Is.Null);
                Assert.That(timedOutProcesses.Started.Count, Is.EqualTo(1));
                Assert.That(error, Does.StartWith("The GameService build timed out"));
            }
        }

        [Test]
        public void ItemToolShowsTheExceptionMessageFirstWhenGameServiceFailsAtStartup()
        {
            const string exception = "Npgsql.PostgresException (0x80004005): 42703: "
                + "column \"sort_order\" does not exist";
            var processes = new FakeToolProcesses();
            var assemblyPath = Path.GetTempFileName();
            try
            {
                using (var operation = DevelopmentItemToolCliProcess.Start(
                    RepositoryRoot,
                    new[] { "--dev-items-list" },
                    processes.Start))
                {
                    CompleteBuild(processes.Started.Single(), assemblyPath);
                    Assert.That(operation.TryComplete(out _, out _), Is.False);
                    processes.Started[1].Result = new DevelopmentToolProcessResult(
                        -532462766,
                        false,
                        "info: Microsoft.Hosting.Lifetime[0]\n      Starting",
                        "Unhandled exception. " + exception + "\n\nPOSITION: 8\n"
                            + "   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async)\n"
                            + "   at Program.<Main>$(String[] args)");

                    Assert.That(operation.TryComplete(out var response, out var error), Is.True);
                    Assert.That(response, Is.Null);
                    Assert.That(
                        error,
                        Is.EqualTo("GameService failed before it returned a development item response: "
                            + exception + "\nGameService exited with code -532462766."));
                    Assert.That(operation.FailureOutput, Does.Contain("at Npgsql.Internal.NpgsqlConnector"));
                    Assert.That(operation.FailureOutput, Does.Contain("Microsoft.Hosting.Lifetime"));
                }
            }
            finally
            {
                File.Delete(assemblyPath);
            }
        }

        [Test]
        public void ItemToolStartupFailureFallsBackToTheFirstErrorLine()
        {
            Assert.That(
                DevelopmentItemToolCli.DescribeStartupFailure(
                    1,
                    "info: startup",
                    "   at Program.<Main>$(String[] args)\nConfiguration is missing.\n"),
                Is.EqualTo("GameService failed before it returned a development item response: "
                    + "Configuration is missing.\nGameService exited with code 1."));
            Assert.That(
                DevelopmentItemToolCli.DescribeStartupFailure(2, string.Empty, string.Empty),
                Is.EqualTo("GameService failed before it returned a development item response.\n"
                    + "GameService exited with code 2."));
        }

        [Test]
        public void ItemToolCancelEndsTheBuildAndNeverStartsTheCommand()
        {
            var processes = new FakeToolProcesses();
            var operation = DevelopmentItemToolCliProcess.Start(
                RepositoryRoot,
                new[] { "--dev-items-list" },
                processes.Start);
            var build = processes.Started.Single();

            operation.Dispose();

            Assert.That(build.Disposed, Is.True);
            Assert.That(processes.Started.Count, Is.EqualTo(1));
            Assert.That(File.Exists(ResultOutputFile(build)), Is.False);
        }

        [Test]
        public void ToolProcessQuotesArgumentsForTheCommandLineParser()
        {
            Assert.That(DevelopmentToolProcess.QuoteArgument("--dev-items-list"), Is.EqualTo("--dev-items-list"));
            Assert.That(DevelopmentToolProcess.QuoteArgument(string.Empty), Is.EqualTo("\"\""));
            Assert.That(
                DevelopmentToolProcess.QuoteArgument(@"C:\My Repo\Game.csproj"),
                Is.EqualTo("\"C:\\My Repo\\Game.csproj\""));
            Assert.That(
                DevelopmentToolProcess.QuoteArgument(@"C:\My Repo\"),
                Is.EqualTo("\"C:\\My Repo\\\\\""));
            Assert.That(
                DevelopmentToolProcess.QuoteArgument("say \"hi\""),
                Is.EqualTo("\"say \\\"hi\\\"\""));
            Assert.That(
                DevelopmentToolProcess.QuoteArgument("a\\\"b c"),
                Is.EqualTo("\"a\\\\\\\"b c\""));
        }

        [Test]
        public void ToolProcessFindsEveryDescendantInTheProcessTable()
        {
            var table = DevelopmentToolProcess.ParseProcessTable(
                "    0     0\n    1     0\n  100     1\n  101   100\r\n  102   101\n"
                    + "  200     1\n  103   100\nnot a process line\n");

            Assert.That(table.Count, Is.EqualTo(7));
            Assert.That(
                DevelopmentToolProcess.FindDescendants(100, table),
                Is.EquivalentTo(new[] { 101, 102, 103 }));
            Assert.That(DevelopmentToolProcess.FindDescendants(200, table), Is.Empty);
            Assert.That(
                DevelopmentToolProcess.FindDescendants(0, table),
                Is.EquivalentTo(new[] { 1, 100, 101, 102, 103, 200 }));
        }

        [Test]
        public void ToolProcessReturnsOutputAndExitCode()
        {
            using (var process = DevelopmentToolProcess.Start(
                ShellCommand("echo tool-output&& exit 3", "echo tool-output; exit 3"),
                TimeSpan.FromSeconds(30)))
            {
                var result = WaitForResult(process);

                Assert.That(result.TimedOut, Is.False);
                Assert.That(result.ExitCode, Is.EqualTo(3));
                Assert.That(result.StandardOutput.Trim(), Is.EqualTo("tool-output"));
            }
        }

        [Test]
        public void ToolProcessTimeoutEndsTheWholeProcessTree()
        {
            // The shell starts a child that inherits the output pipes, so the pipes only
            // close once the child ended too.
            using (var process = DevelopmentToolProcess.Start(
                ShellCommand("ping -n 30 127.0.0.1", "sleep 30; echo done"),
                TimeSpan.FromSeconds(1)))
            {
                var result = WaitForResult(process);
                var waited = Stopwatch.StartNew();
                while (!process.OutputClosed && waited.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(20);
                }

                Assert.That(result.TimedOut, Is.True);
                Assert.That(process.OutputClosed, Is.True, "A child process outlived the timeout.");
            }
        }

        private static void CompleteBuild(FakeToolProcess build, string assemblyPath)
        {
            File.WriteAllText(ResultOutputFile(build), assemblyPath + "\n");
            build.Result = new DevelopmentToolProcessResult(0, false, "Build succeeded.", string.Empty);
        }

        private static string ResultOutputFile(FakeToolProcess build)
        {
            return build.StartInfo.Arguments
                .Single(argument => argument.StartsWith(ResultOutputFileSwitch, StringComparison.Ordinal))
                .Substring(ResultOutputFileSwitch.Length);
        }

        private static DevelopmentToolProcessStartInfo ShellCommand(
            string windowsCommand,
            string posixCommand)
        {
            var windows = Application.platform == RuntimePlatform.WindowsEditor;
            return new DevelopmentToolProcessStartInfo(
                windows ? "cmd.exe" : "/bin/sh",
                windows ? new[] { "/d", "/c", windowsCommand } : new[] { "-c", posixCommand },
                Path.GetTempPath(),
                null);
        }

        private static DevelopmentToolProcessResult WaitForResult(DevelopmentToolProcess process)
        {
            var waited = Stopwatch.StartNew();
            DevelopmentToolProcessResult result;
            while (!process.TryGetResult(out result))
            {
                Assert.That(waited.Elapsed, Is.LessThan(TimeSpan.FromSeconds(20)), "The process did not finish.");
                Thread.Sleep(20);
            }

            return result;
        }

        private sealed class FakeToolProcesses
        {
            public List<FakeToolProcess> Started { get; } = new List<FakeToolProcess>();

            public IDevelopmentToolProcess Start(
                DevelopmentToolProcessStartInfo startInfo,
                TimeSpan timeout)
            {
                var process = new FakeToolProcess(startInfo, timeout);
                Started.Add(process);
                return process;
            }
        }

        private sealed class FakeToolProcess : IDevelopmentToolProcess
        {
            public FakeToolProcess(DevelopmentToolProcessStartInfo startInfo, TimeSpan timeout)
            {
                StartInfo = startInfo;
                Timeout = timeout;
            }

            public DevelopmentToolProcessStartInfo StartInfo { get; }

            public TimeSpan Timeout { get; }

            // Null while the process runs.
            public DevelopmentToolProcessResult Result { get; set; }

            public bool Disposed { get; private set; }

            public bool TryGetResult(out DevelopmentToolProcessResult result)
            {
                result = Result;
                return result != null;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }
    }
}
