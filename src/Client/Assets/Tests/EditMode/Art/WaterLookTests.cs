using Mmorpg.Client.Editor;
using Mmorpg.Client.Worlds;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // A zone gives its water its own colors with a variant of a water material that overrides
    // only them. Rebuilding the water assets keeps the variant's colors and passes every other
    // part of the look on to it.
    public sealed class WaterLookTests
    {
        private const string VariantPath = WaterAssets.Directory + "/M_Water_Lake_TestLook.mat";

        [TearDown]
        public void DeleteVariant()
        {
            AssetDatabase.DeleteAsset(VariantPath);
        }

        [Test]
        public void AWaterLookVariantKeepsItsColorsWhenTheWaterAssetsAreRebuilt()
        {
            var lake = AssetDatabase.LoadAssetAtPath<Material>(WaterAssets.MaterialPath(WaterBodyKind.Lake));
            var deep = new Color(0.01f, 0.07f, 0.13f, 1f);
            var variant = new Material(lake)
            {
                parent = lake
            };
            variant.SetColor("_DeepColor", deep);
            AssetDatabase.CreateAsset(variant, VariantPath);

            WaterAssets.CreateOrUpdate();

            var rebuilt = AssetDatabase.LoadAssetAtPath<Material>(VariantPath);
            Assert.That(rebuilt.parent, Is.EqualTo(lake));
            Assert.That(rebuilt.shader.name, Is.EqualTo(WaterAssets.ShaderName));
            Assert.That(rebuilt.IsPropertyOverriden("_DeepColor"), Is.True);
            Assert.That(((Vector4)(rebuilt.GetColor("_DeepColor") - deep)).magnitude, Is.LessThan(0.0001f));
            Assert.That(rebuilt.IsPropertyOverriden("_ShallowColor"), Is.False);
            Assert.That(
                ((Vector4)(rebuilt.GetColor("_ShallowColor") - lake.GetColor("_ShallowColor"))).magnitude,
                Is.LessThan(0.0001f));
            Assert.That(rebuilt.GetTexture("_Masks"), Is.EqualTo(lake.GetTexture("_Masks")));
        }
    }
}
