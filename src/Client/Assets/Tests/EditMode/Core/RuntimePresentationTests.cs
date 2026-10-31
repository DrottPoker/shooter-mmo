using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class RuntimePresentationTests
    {
        private const string ClientConfigPath = "Assets/Content/Config/ClientConfig.asset";

        // A player build has no default material for URP, so every primitive created at runtime
        // needs the authored material, or it renders with the pink error shader.
        [Test]
        public void TheClientConfigurationCarriesAUrpMaterialForRuntimePrimitives()
        {
            var config = AssetDatabase.LoadAssetAtPath<ClientConfig>(ClientConfigPath);

            Assert.That(config, Is.Not.Null);
            Assert.That(config.RuntimePrimitiveMaterial, Is.Not.Null);
            Assert.That(
                config.RuntimePrimitiveMaterial.shader.name,
                Does.StartWith("Universal Render Pipeline/"));
            Assert.That(config.RuntimePrimitiveMaterial.shader.isSupported, Is.True);
        }

        // The Debug menu draws its lines with this material, color by color, so it must be an
        // unlit URP material with a base color.
        [Test]
        public void TheClientConfigurationCarriesAnUnlitUrpMaterialForDebugLines()
        {
            var config = AssetDatabase.LoadAssetAtPath<ClientConfig>(ClientConfigPath);

            Assert.That(config, Is.Not.Null);
            Assert.That(config.DebugLineMaterial, Is.Not.Null);
            Assert.That(config.DebugLineMaterial.shader.name, Is.EqualTo("Universal Render Pipeline/Unlit"));
            Assert.That(config.DebugLineMaterial.shader.isSupported, Is.True);
            Assert.That(config.DebugLineMaterial.HasProperty("_BaseColor"), Is.True);
        }

        [Test]
        public void RuntimeScriptsCreatePrimitivesOnlyThroughRuntimePrimitives()
        {
            var scriptsRoot = Path.Combine(Application.dataPath, "Scripts");
            var offenders = Directory
                .EnumerateFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(
                    Path.DirectorySeparatorChar + "RuntimePrimitives.cs",
                    StringComparison.Ordinal))
                .Where(path => File.ReadAllText(path).Contains("GameObject.CreatePrimitive("))
                .Select(path => path.Substring(scriptsRoot.Length + 1))
                .ToArray();

            Assert.That(offenders, Is.Empty);
        }

        [Test]
        public void RuntimePrimitivesUseTheGivenMaterial()
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var primitive = RuntimePrimitives.Create(PrimitiveType.Cube, material);
            try
            {
                Assert.That(primitive.GetComponent<Renderer>().sharedMaterial, Is.SameAs(material));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(primitive);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        // Characters move in Update, the camera follows in LateUpdate, and the health bars are
        // placed after the camera, so a bar never trails the head it belongs to.
        [Test]
        public void HealthBarsArePlacedAfterTheCameraFollowedThePlayer()
        {
            var cameraOrder = ExecutionOrder(typeof(OrbitCameraController));
            var hudOrder = ExecutionOrder(typeof(TemporaryCombatHud));

            Assert.That(cameraOrder, Is.GreaterThan(0));
            Assert.That(hudOrder, Is.GreaterThan(cameraOrder));
        }

        private static int ExecutionOrder(Type type)
        {
            var attribute = type.GetCustomAttribute<DefaultExecutionOrder>();
            Assert.That(attribute, Is.Not.Null, type.Name + " has no fixed execution order.");
            return attribute.order;
        }
    }
}
