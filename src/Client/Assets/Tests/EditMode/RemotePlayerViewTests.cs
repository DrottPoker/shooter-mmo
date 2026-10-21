using Mmorpg.Client.Gameplay;
using Mmorpg.GameProtocol;
using NUnit.Framework;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class RemotePlayerViewTests
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Test]
        public void SupportsThePlayerAndTheDevelopmentBotArchetypes()
        {
            Assert.That(RemotePlayerView.SupportsArchetype(RealtimePlayerArchetypeIds.Player), Is.True);
            Assert.That(RemotePlayerView.SupportsArchetype(RealtimePlayerArchetypeIds.DevelopmentBot), Is.True);
            Assert.That(RemotePlayerView.SupportsArchetype("mob.feral_wolf"), Is.False);
            Assert.That(RemotePlayerView.SupportsArchetype(null), Is.False);
        }

        [Test]
        public void ATintedPersonIsOrangeAndATintedDevelopmentBotIsBlue()
        {
            using (var materials = new PlayerBodyTintMaterials())
            {
                AssertColor(
                    SkinColorAfterTint(RealtimePlayerArchetypeIds.Player, true, materials),
                    PlayerBodyAppearance.TintFor(PlayerBodyColors.Player));
                AssertColor(
                    SkinColorAfterTint(RealtimePlayerArchetypeIds.DevelopmentBot, true, materials),
                    PlayerBodyAppearance.TintFor(PlayerBodyColors.DevelopmentBot));
            }

            Assert.That(PlayerBodyColors.Player, Is.Not.EqualTo(PlayerBodyColors.DevelopmentBot));
            Assert.That(PlayerBodyColors.Player, Is.Not.EqualTo(PlayerBodyColors.LocalPlayer));
            Assert.That(PlayerBodyColors.DevelopmentBot, Is.Not.EqualTo(PlayerBodyColors.LocalPlayer));
        }

        [Test]
        public void AnUntintedBodyKeepsItsOwnSkin()
        {
            using (var materials = new PlayerBodyTintMaterials())
            {
                AssertColor(SkinColorAfterTint(RealtimePlayerArchetypeIds.Player, false, materials), Color.white);
                Assert.That(materials.Count, Is.Zero);
            }
        }

        // Colors round-trip through the project's color space, so the channels come back with
        // float rounding.
        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.002f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.002f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.002f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.002f));
        }

        private static Color SkinColorAfterTint(string archetypeId, bool tinted, PlayerBodyTintMaterials materials)
        {
            var root = new GameObject("RemotePlayer");
            var skin = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                var body = PlayerBodyAppearanceTests.CreateBody(root.transform, skin, null);
                var view = root.AddComponent<RemotePlayerView>();
                view.Initialize(7, "character", "Ada", archetypeId, 30, 4, null, null);
                view.ApplyBodyTint(tinted, materials);
                return body.sharedMaterial.GetColor(BaseColorId);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(skin);
            }
        }
    }
}
