using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The local player source tells who the local player is: empty until a player is set, and
    // empty again once its owner clears it or the player is destroyed.
    public sealed class LocalPlayerSourceTests
    {
        [Test]
        public void TheSourceAnnouncesThePlayerAndForgetsOnlyItsOwn()
        {
            var source = new LocalPlayerSource();
            var changes = 0;
            source.Changed += () => changes++;
            var first = new GameObject("First player").AddComponent<LocalPlayerController>();
            var second = new GameObject("Second player").AddComponent<LocalPlayerController>();
            try
            {
                Assert.That(source.HasPlayer, Is.False);
                Assert.That(source.Controller, Is.Null);
                Assert.That(source.Input, Is.Null);

                source.Set(first);
                source.Set(first);
                Assert.That(source.Controller, Is.SameAs(first));
                Assert.That(changes, Is.EqualTo(1), "Setting the same player again changes nothing.");

                source.Set(second);
                source.Clear(first);
                Assert.That(source.Controller, Is.SameAs(second), "A late clear of an older player keeps the newer one.");
                Assert.That(changes, Is.EqualTo(2));

                source.Clear(second);
                Assert.That(source.HasPlayer, Is.False);
                Assert.That(changes, Is.EqualTo(3));

                source.Set(first);
                Object.DestroyImmediate(first.gameObject);
                Assert.That(source.Controller, Is.Null, "A destroyed player reads as none.");
                Assert.That(source.Input, Is.Null);
                source.Clear(first);
                Assert.That(changes, Is.EqualTo(5), "Its owner still clears it.");
            }
            finally
            {
                if (first != null)
                {
                    Object.DestroyImmediate(first.gameObject);
                }

                Object.DestroyImmediate(second.gameObject);
            }
        }
    }
}
