using System;
using System.Collections;
using System.Linq;
using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Characters;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Mmorpg.Client.Tests.PlayMode.ScriptedWorld;

namespace Mmorpg.Client.Tests.PlayMode
{
    // A body shows only once the content of its whole look is loaded, never in underwear and never
    // half dressed: a context of Development World 1 enters the World of a scripted worker, which
    // then sends a player with hair into view and changes its look. The content loads through
    // Addressables, which in the Editor's asset database mode takes a simulated 0.1 s a load, so the
    // frames in between show what a body does while its content loads.
    public sealed class WorldBodyContentPlayModeTests
    {
        private const string Hair = "DarkBrownHair";
        private const string Tattoo = "Tattoo";

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ABodyShowsOnlyOnceTheContentOfItsWholeLookIsLoaded()
        {
            var world = new ScriptedWorld();
            yield return world.Load(null);
            try
            {
                var context = world.Context;
                var transport = world.Transport;

                // The World is entered at once but shown only once the content of the default looks,
                // their underwear, is loaded; a cover hides it meanwhile.
                Assert.That(context.TryEnterWorld(), Is.True);
                Assert.That(context.LocalPlayer, Is.Not.Null);
                Assert.That(context.IsWorldShown, Is.False, "the World shows before the content of its looks loaded");
                Assert.That(GameObject.Find("WorldEntryCover"), Is.Not.Null);
                yield return Until(() => context.IsWorldShown, "the World was never shown");
                Assert.That(GameObject.Find("WorldEntryCover") == null || !GameObject.Find("WorldEntryCover").activeInHierarchy, Is.True);

                // A player with hair comes into view: no body until the hair loaded, then the body
                // with its hair.
                var withHair = Appearance(Choice(CharacterAppearanceFormat.Hair, Hair));
                var cold = Time.realtimeSinceStartup;
                world.Spawn(77, withHair);
                var waited = 0;
                var longest = 0f;
                RemotePlayerView view = null;
                while (view == null || view.Body == null)
                {
                    Assert.That(Time.realtimeSinceStartup - cold, Is.LessThan(10f), "the player's body never showed");
                    yield return null;
                    longest = Mathf.Max(longest, Time.unscaledDeltaTime);
                    if (view == null)
                    {
                        context.TryGetRemotePlayer(77, out view);
                    }

                    waited += view != null && view.Body == null ? 1 : 0;
                }

                var coldSeconds = Time.realtimeSinceStartup - cold;
                // The frames in which the first garment of its shader variant draws, after the
                // warm-up drew that variant once.
                for (var frame = 0; frame < 5; frame++)
                {
                    yield return null;
                    longest = Mathf.Max(longest, Time.unscaledDeltaTime);
                }

                Assert.That(waited, Is.GreaterThan(0), "the body did not wait for the content of its look");
                var body = view.GetComponentInChildren<PlayerBodyAppearance>();
                AssertWearsItsLook(body);
                Assert.That(body.Worn.Select(garment => garment.Name), Does.Contain(Hair));

                // The look changes to one with a tattoo and another face: the body keeps its old look
                // until the new one's content is loaded.
                var tattooed = Appearance(
                    Choice(CharacterAppearanceFormat.Hair, Hair),
                    Choice(CharacterAppearanceFormat.Tattoo, Tattoo),
                    Choice(CharacterAppearanceFormat.Face, "Clean"));
                var oldLook = body.Look;
                transport.Deliver(RealtimeProtocol.EncodeEntityAppearanceChanged(new RealtimeEntityAppearanceChanged(77, tattooed)),
                    RealtimeProtocol.WorldChannel);
                var kept = 0;
                var started = Time.realtimeSinceStartup;
                while (!body.Look.HasPaint(Tattoo))
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the new look never showed");
                    Assert.That(body.Look.Key, Is.EqualTo(oldLook.Key), "the body changed its look before the content loaded");
                    AssertWearsItsLook(body);
                    kept++;
                    yield return null;
                }

                Assert.That(kept, Is.GreaterThan(0), "the body did not keep its old look while the new one loaded");
                AssertWearsItsLook(body);
                var painted = body.CombinedRenderer.sharedMaterials.Any(material =>
                    material.HasProperty("_PaintMap1") && material.GetTexture("_PaintMap1") != null
                    && material.GetTexture("_PaintMap1").name.StartsWith("T_Paint_Tattoo", StringComparison.Ordinal));
                Assert.That(painted, Is.True, "the new look shows its tattoo");

                // A second player in a look whose content is loaded shows as soon as its body is
                // built, without a load.
                var warm = Time.realtimeSinceStartup;
                world.Spawn(78, withHair);
                RemotePlayerView second = null;
                while (second == null || second.Body == null)
                {
                    Assert.That(Time.realtimeSinceStartup - warm, Is.LessThan(10f), "the second body never showed");
                    yield return null;
                    if (second == null)
                    {
                        context.TryGetRemotePlayer(78, out second);
                    }
                }

                var warmSeconds = Time.realtimeSinceStartup - warm;
                AssertWearsItsLook(second.GetComponentInChildren<PlayerBodyAppearance>());
                Debug.Log("[BODY CONTENT] A body came into view and showed after "
                    + (coldSeconds * 1000f).ToString("0") + " ms with its content cold and "
                    + (warmSeconds * 1000f).ToString("0") + " ms with it loaded; the longest frame from the cold spawn until 5 frames after it showed took "
                    + (longest * 1000f).ToString("0.0") + " ms; garments "
                    + context.BodyGarmentCount + ", attachments " + context.BodyAttachmentCount + ".");
                Assert.That(context.BodyGarmentCount, Is.GreaterThan(0));
            }
            finally
            {
                world.Dispose();
            }
        }

        // A player in view at the entry: the cover stays until the content of its look is loaded,
        // and the World shows with it.
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator TheWorldShowsOnceTheContentOfThePlayersInViewAtTheEntryIsLoaded()
        {
            var world = new ScriptedWorld();
            yield return world.Load(null);
            try
            {
                var context = world.Context;
                context.FirstViewCap = 30f;
                world.Spawn(77, Appearance(Choice(CharacterAppearanceFormat.Hair, Hair)));
                Assert.That(context.TryEnterWorld(), Is.True);
                var hair = HairAddress(context);
                var started = Time.realtimeSinceStartup;
                while (!context.IsWorldShown)
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the World was never shown");
                    yield return null;
                }

                Assert.That(context.BodyContent.PieceOf(hair), Is.Not.Null, "the World showed before the content of a player in view loaded");
                Assert.That(context.ShownFrame, Is.GreaterThan(context.EntryLookFrame));
            }
            finally
            {
                world.Dispose();
            }
        }

        // A part of the first view that never gets ready holds the cover only until the cap.
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator TheWorldShowsAtTheCapWhenTheFirstViewIsNotReady()
        {
            var world = new ScriptedWorld();
            yield return world.Load(null);
            try
            {
                var context = world.Context;
                const float cap = 0.5f;
                context.FirstViewCap = cap;
                context.AddEntryReadiness(new NeverReady());
                var started = Time.realtimeSinceStartup;
                Assert.That(context.TryEnterWorld(), Is.True);
                while (!context.IsWorldShown)
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the World was never shown");
                    yield return null;
                }

                Assert.That(Time.realtimeSinceStartup - started, Is.GreaterThanOrEqualTo(cap), "the World showed before its cap");
                Assert.That(GameObject.Find("WorldEntryCover") == null || !GameObject.Find("WorldEntryCover").activeInHierarchy, Is.True);
            }
            finally
            {
                world.Dispose();
            }
        }

        // Players in view at the entry whose bodies were not ready when the World showed at its cap
        // come into view afterwards within the presentation budget, each with its whole look: with a
        // budget that one body spends, putting a body on takes milliseconds, one a frame.
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator WhatWasNotReadyAtTheCapComesIntoViewWithinThePresentationBudget()
        {
            var world = new ScriptedWorld();
            yield return world.Load(new PresentationBudget(0.5, null));
            try
            {
                var context = world.Context;
                context.FirstViewCap = 0f;
                var withHair = Appearance(Choice(CharacterAppearanceFormat.Hair, Hair));
                var players = new ulong[] { 77, 78, 79, 80, 81, 82 };
                foreach (var player in players)
                {
                    world.Spawn(player, withHair);
                }

                Assert.That(context.TryEnterWorld(), Is.True);
                var started = Time.realtimeSinceStartup;
                while (!context.IsWorldShown)
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the World was never shown");
                    yield return null;
                }

                var withBodyAtTheCap = players.Count(player => context.TryGetRemotePlayer(player, out var view) && view.Body != null);
                Assert.That(withBodyAtTheCap, Is.LessThan(players.Length), "every body was ready at the cap");
                var shownFrames = new System.Collections.Generic.Dictionary<ulong, int>();
                while (shownFrames.Count < players.Length)
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "a body never came into view");
                    foreach (var player in players)
                    {
                        if (!shownFrames.ContainsKey(player) && context.TryGetRemotePlayer(player, out var view) && view.Body != null)
                        {
                            shownFrames[player] = Time.frameCount;
                            AssertWearsItsLook(view.GetComponentInChildren<PlayerBodyAppearance>());
                        }
                    }

                    yield return null;
                }

                Assert.That(shownFrames.Values.Distinct().Count(), Is.EqualTo(players.Length), "two bodies went on in one frame of a spent budget");
            }
            finally
            {
                world.Dispose();
            }
        }

        // Without other players or actors in view the World shows in the frame after the content of
        // the local look loaded: the warm-up draws the bodies once under the cover in that frame.
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator AWorldWithNobodyElseInViewShowsTheFrameAfterItsLookLoaded()
        {
            var world = new ScriptedWorld();
            yield return world.Load(null);
            try
            {
                var context = world.Context;
                Assert.That(context.TryEnterWorld(), Is.True);
                var started = Time.realtimeSinceStartup;
                while (!context.IsWorldShown)
                {
                    Assert.That(Time.realtimeSinceStartup - started, Is.LessThan(10f), "the World was never shown");
                    yield return null;
                }

                Assert.That(context.ShownFrame - context.EntryLookFrame, Is.EqualTo(1));
            }
            finally
            {
                world.Dispose();
            }
        }

        // The address of the hair's content for the local player's body model, which the visitors
        // share.
        private static string HairAddress(WorldSceneContext context)
        {
            var catalog = context.LocalPlayer.GetComponentInChildren<PlayerBodyAppearance>(true).Catalog;
            return catalog.Garments.First(garment => garment.Name == Hair).Address;
        }

        private sealed class NeverReady : IWorldEntryReadiness
        {
            public bool IsReadyForEntry
            {
                get { return false; }
            }
        }

        // A body that shows wears every garment its look names that its catalog has, so no frame
        // shows it half dressed.
        private static void AssertWearsItsLook(PlayerBodyAppearance body)
        {
            Assert.That(body, Is.Not.Null);
            Assert.That(body.CombinedRenderer, Is.Not.Null);
            Assert.That(body.CombinedRenderer.sharedMesh, Is.Not.Null);
            var named = PlayerBodyAppearance.WornBy(body.Catalog, body.Look).Select(garment => garment.Name).ToArray();
            Assert.That(body.Worn.Select(garment => garment.Name), Is.EqualTo(named), "the body shows without a garment of its look");
        }
    }
}
