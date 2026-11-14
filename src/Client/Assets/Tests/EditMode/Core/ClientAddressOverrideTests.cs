using Mmorpg.Client.Core;
using NUnit.Framework;

namespace Mmorpg.Client.Tests.EditMode
{
    // A development player takes the GameService address from its command line, the Editor from
    // its switch, and a release build from neither.
    public sealed class ClientAddressOverrideTests
    {
        private static readonly string[] CommandLine =
        {
            "MMORPG.exe",
            "-logFile",
            "player.log",
            ClientAddressOverride.CommandLineArgument,
            "http://192.168.1.20:5000/"
        };

        [Test]
        public void ADevelopmentPlayerTakesTheAddressOfItsCommandLine()
        {
            var address = ClientAddressOverride.Resolve(ClientBuildKind.Development, CommandLine, "http://ignored:1", out var diagnostic);
            Assert.That(address, Is.EqualTo("http://192.168.1.20:5000"));
            Assert.That(diagnostic, Does.Contain("overridden at start"));
        }

        [Test]
        public void AReleasePlayerIgnoresEveryOverride()
        {
            var address = ClientAddressOverride.Resolve(ClientBuildKind.Release, CommandLine, "http://ignored:1", out var diagnostic);
            Assert.That(address, Is.Empty);
            Assert.That(diagnostic, Does.Contain("release build ignores"));

            Assert.That(ClientAddressOverride.Resolve(ClientBuildKind.Release, new[] { "MMORPG.exe" }, null, out diagnostic), Is.Empty);
            Assert.That(diagnostic, Is.Empty);
        }

        [Test]
        public void TheEditorTakesItsSwitchAndNotTheCommandLine()
        {
            Assert.That(
                ClientAddressOverride.Resolve(ClientBuildKind.Editor, CommandLine, "https://staging.example.org/", out _),
                Is.EqualTo("https://staging.example.org"));
            Assert.That(ClientAddressOverride.Resolve(ClientBuildKind.Editor, CommandLine, string.Empty, out var diagnostic), Is.Empty);
            Assert.That(diagnostic, Is.Empty);
        }

        [Test]
        public void AnAddressThatIsNoHttpUrlIsIgnored()
        {
            var address = ClientAddressOverride.Resolve(
                ClientBuildKind.Development,
                new[] { "MMORPG.exe", ClientAddressOverride.CommandLineArgument, "192.168.1.20:5000" },
                null,
                out var diagnostic);
            Assert.That(address, Is.Empty);
            Assert.That(diagnostic, Does.Contain("ignored"));

            Assert.That(
                ClientAddressOverride.Resolve(
                    ClientBuildKind.Development,
                    new[] { "MMORPG.exe", ClientAddressOverride.CommandLineArgument },
                    null,
                    out diagnostic),
                Is.Empty);
            Assert.That(diagnostic, Is.Empty);
        }

        [Test]
        public void TheLastArgumentWins()
        {
            var address = ClientAddressOverride.Resolve(
                ClientBuildKind.Development,
                new[]
                {
                    "MMORPG.exe",
                    ClientAddressOverride.CommandLineArgument, "http://first:5000",
                    ClientAddressOverride.CommandLineArgument, "http://second:5000"
                },
                null,
                out _);
            Assert.That(address, Is.EqualTo("http://second:5000"));
        }
    }
}
