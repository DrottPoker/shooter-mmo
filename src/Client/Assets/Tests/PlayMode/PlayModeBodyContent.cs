using System.Collections;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.PlayMode
{
    // The content of a look for a PlayMode test, loaded through Addressables as a World loads it:
    // asked for once a frame until it is ready, without waiting for a load inside a frame.
    internal static class PlayModeBodyContent
    {
        public static IEnumerator Load(PlayerBodyContent content, BodyAppearanceCatalog catalog, BodyLook look, float seconds = 10f)
        {
            var needs = PlayerBodyContent.NeedsOf(catalog, look);
            var deadline = Time.realtimeSinceStartup + seconds;
            content.Update();
            while (!content.Want(needs))
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(deadline), "The content of the look " + look.Key + " did not load.");
                yield return null;
                content.Update();
            }
        }
    }
}
