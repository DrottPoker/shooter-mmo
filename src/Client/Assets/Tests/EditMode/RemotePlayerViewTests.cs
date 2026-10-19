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
        public void APersonIsOrangeAndADevelopmentBotIsBlue()
        {
            AssertColor(BodyColorAfterInitialize(RealtimePlayerArchetypeIds.Player), RemotePlayerView.PlayerBodyColor);
            AssertColor(
                BodyColorAfterInitialize(RealtimePlayerArchetypeIds.DevelopmentBot),
                RemotePlayerView.DevelopmentBotBodyColor);
            Assert.That(RemotePlayerView.PlayerBodyColor, Is.Not.EqualTo(RemotePlayerView.DevelopmentBotBodyColor));
        }

        // A property block round-trips colors through the project's color space, so the
        // channels come back with float rounding.
        private static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.002f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.002f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.002f));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(0.002f));
        }

        private static Color BodyColorAfterInitialize(string archetypeId)
        {
            var root = new GameObject("RemotePlayer");
            try
            {
                var visual = new GameObject("PlayerVisual");
                visual.transform.SetParent(root.transform, false);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Body";
                Object.DestroyImmediate(body.GetComponent<Collider>());
                body.transform.SetParent(visual.transform, false);
                var view = root.AddComponent<RemotePlayerView>();

                view.Initialize(7, "character", "Ada", archetypeId, 30, 3, null, null);

                var properties = new MaterialPropertyBlock();
                body.GetComponent<Renderer>().GetPropertyBlock(properties);
                return properties.GetColor(BaseColorId);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
