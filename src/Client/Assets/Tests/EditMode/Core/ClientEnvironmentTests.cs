using System;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The environment files own where a client connects, and the Editor runs the local one.
    public sealed class ClientEnvironmentTests
    {
        private static string EnvironmentFolder
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ClientEnvironment.FolderName)); }
        }

        [Test]
        public void EveryEnvironmentFileIsValidAndNamedAfterItsFile()
        {
            var files = Directory.GetFiles(EnvironmentFolder, "*.json");
            Assert.That(files.Select(Path.GetFileNameWithoutExtension), Does.Contain(ClientEnvironment.LocalName));
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                Assert.That(
                    ClientEnvironment.TryParse(File.ReadAllText(file), name, out var environment, out var error),
                    Is.True,
                    file + ": " + error);
                Assert.That(environment.Name, Is.EqualTo(name));
            }

            Assert.That(
                Directory.GetFiles(EnvironmentFolder).Where(file => !file.EndsWith(".json", StringComparison.Ordinal)),
                Is.Empty,
                "The environment folder holds only environment files.");
        }

        // The local environment is the stack of Local Development on this machine.
        [Test]
        public void TheLocalEnvironmentIsTheLocalStack()
        {
            var local = ReadLocal();
            Assert.That(local.GameServiceBaseUrl, Is.EqualTo("http://localhost:5000"));
        }

        // The Editor needs no build step: it reads local.json itself, so the address the Editor
        // and every test of it use is the one in that file.
        [Test]
        public void TheEditorRunsTheLocalEnvironmentFile()
        {
            var info = ClientBuildInfo.Load();
            var local = ReadLocal();

            Assert.That(info.Environment.Name, Is.EqualTo(ClientEnvironment.LocalName));
            Assert.That(info.Environment.GameServiceBaseUrl, Is.EqualTo(local.GameServiceBaseUrl));
            Assert.That(info.Stamp, Is.SameAs(ClientBuildStamp.Editor));
            Assert.That(info.Stamp.HasDevelopmentTools, Is.True);
        }

        // The environment owns the server address; the configuration keeps only the timeouts.
        [Test]
        public void TheClientConfigurationHoldsNoServerAddress()
        {
            var configPath = Path.Combine(Application.dataPath, "Content", "Config", "ClientConfig.asset");
            Assert.That(File.ReadAllText(configPath), Does.Not.Contain("gameServiceBaseUrl"));
            Assert.That(typeof(ClientConfig).GetProperty("GameServiceBaseUrl"), Is.Null);
        }

        [Test]
        public void AnAddressIsAnAbsoluteHttpUrlWithoutItsTrailingSlash()
        {
            Assert.That(ClientEnvironment.TryNormalizeAddress("http://localhost:5000/", out var address), Is.True);
            Assert.That(address, Is.EqualTo("http://localhost:5000"));
            Assert.That(ClientEnvironment.TryNormalizeAddress(" https://game.example.org/api ", out address), Is.True);
            Assert.That(address, Is.EqualTo("https://game.example.org/api"));

            foreach (var invalid in new[]
                     {
                         null,
                         string.Empty,
                         "localhost:5000",
                         "/api",
                         "ftp://game.example.org",
                         "http://user:secret@game.example.org",
                         "http://game.example.org/?shard=1",
                         "http://game.example.org/#top"
                     })
            {
                Assert.That(ClientEnvironment.TryNormalizeAddress(invalid, out address), Is.False, invalid);
                Assert.That(address, Is.Empty, invalid);
            }
        }

        [Test]
        public void AFileMustNameTheEnvironmentItIs()
        {
            const string json = "{\"name\":\"staging\",\"gameServiceBaseUrl\":\"https://staging.example.org\"}";
            Assert.That(ClientEnvironment.TryParse(json, "staging", out var environment, out _), Is.True);
            Assert.That(environment.GameServiceBaseUrl, Is.EqualTo("https://staging.example.org"));

            Assert.That(ClientEnvironment.TryParse(json, "local", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("staging"));
            Assert.That(
                ClientEnvironment.TryParse("{\"name\":\"Staging\",\"gameServiceBaseUrl\":\"https://staging.example.org\"}", null, out _, out _),
                Is.False);
            Assert.That(ClientEnvironment.TryParse("{\"name\":\"staging\"}", null, out _, out _), Is.False);
            Assert.That(ClientEnvironment.TryParse("not json", null, out _, out _), Is.False);
            Assert.That(ClientEnvironment.TryParse(string.Empty, null, out _, out _), Is.False);
        }

        private static ClientEnvironment ReadLocal()
        {
            var path = Path.Combine(EnvironmentFolder, ClientEnvironment.LocalName + ".json");
            Assert.That(
                ClientEnvironment.TryParse(File.ReadAllText(path), ClientEnvironment.LocalName, out var local, out var error),
                Is.True,
                error);
            return local;
        }
    }
}
