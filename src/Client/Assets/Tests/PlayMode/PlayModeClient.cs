using System;
using System.Collections;
using Mmorpg.Client.App;
using Mmorpg.Client.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The client resolves its content once when Play Mode starts, and creates its controllers
    // and composes scenes only after that. Without a GameService the manifest request fails
    // within its short limit, so the wait is short. Tests reach the running client's session
    // here, to sign in and select as the entry screens do.
    internal static class PlayModeClient
    {
        // The whole content check ends within 20 seconds; the rest is headroom for a slow frame.
        private const float ContentWaitSeconds = 30f;

        // The session of the running client, which its bootstrap creates when Play Mode starts.
        public static ClientSession Session
        {
            get
            {
                var bootstrap = Object.FindAnyObjectByType<ClientBootstrap>();
                Assert.That(bootstrap, Is.Not.Null, "The client bootstrap is not running.");
                return bootstrap.Session;
            }
        }

        public static IEnumerator WaitForContent()
        {
            var deadline = Time.realtimeSinceStartup + ContentWaitSeconds;
            while (true)
            {
                var bootstrap = Object.FindAnyObjectByType<ClientBootstrap>();
                if (bootstrap != null && bootstrap.IsContentReady)
                {
                    yield break;
                }

                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "The client did not resolve its content.");
                yield return null;
            }
        }

        // Points the running client at another GameService, such as an unreachable one, until
        // the result is disposed.
        public static IDisposable UseGameService(string baseUrl)
        {
            var session = Session;
            var previous = session.Settings;
            session.UseSettingsForTests(previous.WithGameServiceBaseUrl(baseUrl));
            return new SettingsRestore(session, previous);
        }

        private sealed class SettingsRestore : IDisposable
        {
            private readonly ClientSession session;
            private readonly ClientSettings previous;

            public SettingsRestore(ClientSession session, ClientSettings previous)
            {
                this.session = session;
                this.previous = previous;
            }

            public void Dispose()
            {
                session.UseSettingsForTests(previous);
            }
        }
    }
}
