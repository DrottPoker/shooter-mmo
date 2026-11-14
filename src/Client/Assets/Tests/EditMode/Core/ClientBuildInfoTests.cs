using System;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The build stamp and the environment that a player build bakes, and what each kind of client
    // takes from them.
    public sealed class ClientBuildInfoTests
    {
        private static readonly ClientEnvironment Staging =
            new ClientEnvironment("staging", "https://staging.example.org");

        [Test]
        public void ABakedBuildReadsBackAsItWasWritten()
        {
            foreach (var kind in new[] { ClientBuildKind.Development, ClientBuildKind.Release })
            {
                var written = new ClientBuildInfo(
                    Staging,
                    new ClientBuildStamp(kind, "0123456789abcdef0123456789abcdef01234567", true, "2026-11-14T09:30:00Z"));

                Assert.That(ClientBuildInfo.TryParse(written.ToJson(), out var read, out var error), Is.True, error);
                Assert.That(read.Environment.Name, Is.EqualTo("staging"));
                Assert.That(read.Environment.GameServiceBaseUrl, Is.EqualTo("https://staging.example.org"));
                Assert.That(read.Stamp.Kind, Is.EqualTo(kind));
                Assert.That(read.Stamp.Commit, Is.EqualTo("0123456789abcdef0123456789abcdef01234567"));
                Assert.That(read.Stamp.Dirty, Is.True);
                Assert.That(read.Stamp.BuiltAtUtc, Is.EqualTo("2026-11-14T09:30:00Z"));
            }
        }

        [Test]
        public void TheEditorStampIsNeverBaked()
        {
            Assert.Throws<InvalidOperationException>(() => new ClientBuildInfo(Staging, ClientBuildStamp.Editor).ToJson());
        }

        [Test]
        public void ABuildFileThatIsNotWholeIsRefused()
        {
            var valid = new ClientBuildInfo(
                Staging,
                new ClientBuildStamp(ClientBuildKind.Release, "abc", false, "2026-11-14T09:30:00Z")).ToJson();
            Assert.That(ClientBuildInfo.TryParse(valid, out _, out _), Is.True);

            foreach (var (from, to) in new[]
                     {
                         ("\"formatVersion\": 1", "\"formatVersion\": 2"),
                         ("\"kind\": \"release\"", "\"kind\": \"editor\""),
                         ("\"builtAtUtc\": \"2026-11-14T09:30:00Z\"", "\"builtAtUtc\": \"yesterday\""),
                         ("https://staging.example.org", "staging.example.org"),
                         ("\"name\": \"staging\"", "\"name\": \"\"")
                     })
            {
                Assert.That(valid, Does.Contain(from));
                Assert.That(ClientBuildInfo.TryParse(valid.Replace(from, to), out var info, out var error), Is.False, to);
                Assert.That(info, Is.Null);
                Assert.That(error, Is.Not.Empty);
            }

            Assert.That(ClientBuildInfo.TryParse("{}", out _, out _), Is.False);
            Assert.That(ClientBuildInfo.TryParse(string.Empty, out _, out _), Is.False);
        }

        // A release build has none of the development tools: no Admin menu and no override of
        // the GameService address. The Editor and development builds have them.
        [Test]
        public void OnlyTheEditorAndDevelopmentBuildsHaveTheDevelopmentTools()
        {
            Assert.That(ClientBuildStamp.Editor.HasDevelopmentTools, Is.True);
            Assert.That(Stamp(ClientBuildKind.Development).HasDevelopmentTools, Is.True);
            Assert.That(Stamp(ClientBuildKind.Release).HasDevelopmentTools, Is.False);
        }

        [Test]
        public void AStampDescribesItselfInOneLine()
        {
            var stamp = new ClientBuildStamp(ClientBuildKind.Development, "0123456789abcdef", true, "2026-11-14T09:30:00Z");
            Assert.That(stamp.ShortCommit, Is.EqualTo("0123456789"));
            Assert.That(stamp.BuiltAtDisplay, Is.EqualTo("2026-11-14 09:30"));
            Assert.That(
                stamp.Describe(),
                Is.EqualTo("development build 0123456789 (uncommitted changes), built 2026-11-14 09:30 UTC"));
            Assert.That(
                new ClientBuildStamp(ClientBuildKind.Release, string.Empty, false, "2026-11-14T09:30:00Z").Describe(),
                Is.EqualTo("release build unknown, built 2026-11-14 09:30 UTC"));
            Assert.That(ClientBuildStamp.Editor.Describe(), Is.EqualTo("Unity Editor"));
        }

        // The baked file exists only while a build runs, and Git never sees it.
        [Test]
        public void TheBakedBuildFileIsIgnoredByGit()
        {
            var gitignore = File.ReadAllLines(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", ".gitignore")));
            var path = "src/Client/Assets/Content/" + ClientAddresses.ClientBuild;
            Assert.That(gitignore, Does.Contain(path));
            Assert.That(gitignore, Does.Contain(path + ".meta"));
            Assert.That(File.Exists(Path.Combine(Application.dataPath, "Content", ClientAddresses.ClientBuild)), Is.False);
        }

        [Test]
        public void TheSettingsCarryTheEnvironmentTheStampAndTheOverride()
        {
            var info = new ClientBuildInfo(Staging, Stamp(ClientBuildKind.Development));
            var plain = new ClientSettings(info, string.Empty, 10, 10, 5);
            Assert.That(plain.Environment, Is.SameAs(Staging));
            Assert.That(plain.Build.Kind, Is.EqualTo(ClientBuildKind.Development));
            Assert.That(plain.GameServiceBaseUrl, Is.EqualTo("https://staging.example.org"));
            Assert.That(plain.IsGameServiceOverridden, Is.False);

            var overridden = new ClientSettings(info, "http://192.168.1.20:5000/", 10, 10, 5);
            Assert.That(overridden.GameServiceBaseUrl, Is.EqualTo("http://192.168.1.20:5000"));
            Assert.That(overridden.GameServiceOverride, Is.EqualTo("http://192.168.1.20:5000"));
            Assert.That(overridden.Environment.GameServiceBaseUrl, Is.EqualTo("https://staging.example.org"));
            Assert.That(overridden.IsGameServiceOverridden, Is.True);
            Assert.Throws<ArgumentException>(() => new ClientSettings(info, "not a url", 10, 10, 5));
        }

        private static ClientBuildStamp Stamp(ClientBuildKind kind)
        {
            return new ClientBuildStamp(kind, "abc", false, "2026-11-14T09:30:00Z");
        }
    }
}
