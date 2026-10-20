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
            using (var materials = new RemotePlayerBodyMaterials())
            {
                AssertColor(
                    BodyColorAfterInitialize(RealtimePlayerArchetypeIds.Player, materials),
                    RemotePlayerView.PlayerBodyColor);
                AssertColor(
                    BodyColorAfterInitialize(RealtimePlayerArchetypeIds.DevelopmentBot, materials),
                    RemotePlayerView.DevelopmentBotBodyColor);
            }

            Assert.That(RemotePlayerView.PlayerBodyColor, Is.Not.EqualTo(RemotePlayerView.DevelopmentBotBodyColor));
        }

        [Test]
        public void BodiesOfOneColorShareOneMaterialWithoutAPropertyBlock()
        {
            var source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var first = new GameObject("First");
            var second = new GameObject("Second");
            try
            {
                using (var materials = new RemotePlayerBodyMaterials())
                {
                    var firstBody = CreateView(first, RealtimePlayerArchetypeIds.DevelopmentBot, source, materials);
                    var secondBody = CreateView(second, RealtimePlayerArchetypeIds.DevelopmentBot, source, materials);

                    Assert.That(firstBody.sharedMaterial, Is.SameAs(secondBody.sharedMaterial));
                    Assert.That(firstBody.sharedMaterial, Is.Not.SameAs(source));
                    Assert.That(firstBody.HasPropertyBlock(), Is.False);
                    Assert.That(materials.Count, Is.EqualTo(1));
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(source);
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

        private static Color BodyColorAfterInitialize(string archetypeId, RemotePlayerBodyMaterials materials)
        {
            var root = new GameObject("RemotePlayer");
            var source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                return CreateView(root, archetypeId, source, materials).sharedMaterial.GetColor(BaseColorId);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(source);
            }
        }

        private static Renderer CreateView(
            GameObject root,
            string archetypeId,
            Material bodyMaterial,
            RemotePlayerBodyMaterials materials)
        {
            var visual = new GameObject("PlayerVisual");
            visual.transform.SetParent(root.transform, false);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(visual.transform, false);
            var bodyRenderer = body.GetComponent<Renderer>();
            bodyRenderer.sharedMaterial = bodyMaterial;
            var view = root.AddComponent<RemotePlayerView>();

            view.Initialize(7, "character", "Ada", archetypeId, 30, 4, null, null, materials);
            return bodyRenderer;
        }
    }
}
