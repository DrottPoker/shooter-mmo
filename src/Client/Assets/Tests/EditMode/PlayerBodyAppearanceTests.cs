using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class PlayerBodyAppearanceTests
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Test]
        public void ATintMultipliesTheSkinAndLeavesOtherMaterials()
        {
            var root = new GameObject("Body");
            var skin = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var eye = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            skin.SetColor(BaseColorId, new Color(0.5f, 1f, 1f, 1f));
            try
            {
                using (var materials = new PlayerBodyTintMaterials())
                {
                    var head = CreateBody(root.transform, skin, eye);
                    var appearance = root.GetComponentInChildren<PlayerBodyAppearance>();
                    var tint = PlayerBodyAppearance.TintFor(PlayerBodyColors.LocalPlayer);

                    appearance.ApplyTint(true, PlayerBodyColors.LocalPlayer, materials);

                    var tinted = head.sharedMaterials;
                    Assert.That(tinted[0], Is.Not.SameAs(skin));
                    Assert.That(tinted[0].GetColor(BaseColorId).r, Is.EqualTo(0.5f * tint.r).Within(0.002f));
                    Assert.That(tinted[0].GetColor(BaseColorId).g, Is.EqualTo(tint.g).Within(0.002f));
                    Assert.That(tinted[1], Is.SameAs(eye));
                    Assert.That(head.HasPropertyBlock(), Is.False);

                    appearance.ApplyTint(false, PlayerBodyColors.LocalPlayer, materials);

                    Assert.That(head.sharedMaterials, Is.EqualTo(new[] { skin, eye }));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(skin);
                Object.DestroyImmediate(eye);
            }
        }

        [Test]
        public void BodiesWithOneTintShareOneMaterial()
        {
            var first = new GameObject("First");
            var second = new GameObject("Second");
            var skin = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                using (var materials = new PlayerBodyTintMaterials())
                {
                    var firstBody = CreateBody(first.transform, skin, null);
                    var secondBody = CreateBody(second.transform, skin, null);
                    first.GetComponentInChildren<PlayerBodyAppearance>().ApplyTint(true, PlayerBodyColors.DevelopmentBot, materials);
                    second.GetComponentInChildren<PlayerBodyAppearance>().ApplyTint(true, PlayerBodyColors.DevelopmentBot, materials);

                    Assert.That(firstBody.sharedMaterial, Is.SameAs(secondBody.sharedMaterial));
                    Assert.That(materials.Count, Is.EqualTo(1));
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(skin);
            }
        }

        [Test]
        public void TheTintIsFaint()
        {
            var tint = PlayerBodyAppearance.TintFor(PlayerBodyColors.Player);
            Assert.That(tint, Is.EqualTo(Color.Lerp(Color.white, PlayerBodyColors.Player, PlayerBodyAppearance.TintStrength)));
            Assert.That(PlayerBodyAppearance.TintStrength, Is.InRange(0.2f, 0.5f));
        }

        // A body below the parent with a PlayerBodyAppearance whose skin is the given material,
        // and one renderer with the skin and, if given, a second material.
        internal static Renderer CreateBody(Transform parent, Material skin, Material other)
        {
            var bodyRoot = new GameObject("PlayerBody");
            bodyRoot.transform.SetParent(parent, false);
            var appearance = bodyRoot.AddComponent<PlayerBodyAppearance>();
            var properties = new SerializedObject(appearance);
            var skinMaterials = properties.FindProperty("skinMaterials");
            skinMaterials.arraySize = 1;
            skinMaterials.GetArrayElementAtIndex(0).objectReferenceValue = skin;
            properties.ApplyModifiedPropertiesWithoutUndo();

            var mesh = new GameObject("Head");
            mesh.transform.SetParent(bodyRoot.transform, false);
            var meshRenderer = mesh.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = other != null ? new[] { skin, other } : new[] { skin };
            return meshRenderer;
        }
    }
}
