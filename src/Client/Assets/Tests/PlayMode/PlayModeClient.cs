using System.Collections;
using Mmorpg.Client.App;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The client resolves its content once when Play Mode starts, and creates its controllers
    // and composes scenes only after that. Without a GameService the manifest request fails
    // within its short limit, so the wait is short.
    internal static class PlayModeClient
    {
        // The whole content check ends within 20 seconds; the rest is headroom for a slow frame.
        private const float ContentWaitSeconds = 30f;

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
    }
}
