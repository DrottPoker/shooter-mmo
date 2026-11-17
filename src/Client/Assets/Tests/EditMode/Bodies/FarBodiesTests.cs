using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Tests.EditMode
{
    // Another player's body beyond 44 m from the camera casts no shadow until it comes within 40 m
    // again, decided a few times a second at a time of its own, as docs/UNITY_CLIENT_ARCHITECTURE.md
    // describes under Crowd Performance.
    public sealed class FarBodiesTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        [Test]
        public void ABodyCastsNoShadowBeyond44MetersUntilItComesWithin40()
        {
            Assert.That(FarBodies.CastsShadowAt(10f, true), Is.True);
            Assert.That(FarBodies.CastsShadowAt(43.9f, true), Is.True, "a body that casts keeps its shadow up to 44 m");
            Assert.That(FarBodies.CastsShadowAt(44f, true), Is.False);
            Assert.That(FarBodies.CastsShadowAt(60f, true), Is.False);
            Assert.That(FarBodies.CastsShadowAt(40f, false), Is.False, "a body without a shadow takes it back only within 40 m");
            Assert.That(FarBodies.CastsShadowAt(42f, false), Is.False);
            Assert.That(FarBodies.CastsShadowAt(39.9f, false), Is.True);
        }

        [Test]
        public void TheChecksOfTheBodiesSpreadOverTheirPeriod()
        {
            var offsets = Enumerable.Range(0, 16).Select(id => FarBodies.CheckOffsetOf((ulong)id)).ToArray();
            Assert.That(offsets.Distinct().Count(), Is.EqualTo(16));
            Assert.That(offsets, Has.All.InRange(0f, 1f / FarBodies.ChecksPerSecond));
        }

        // The body of another player casts its shadow from its one renderer, which draws its
        // garments and the attachments it holds, and stops and starts by the boundaries; the
        // check allocates nothing.
        [Test]
        public void ARemoteBodyCastsItsShadowByItsDistanceWithoutAllocating()
        {
            var root = new GameObject("RemotePlayer");
            try
            {
                using (var content = TestBodyContent.Loaded())
                using (var meshes = new PlayerBodyMeshes(false, content))
                {
                    var body = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath), root.transform, false);
                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var renderer = appearance.CombinedRenderer;
                    Assert.That(renderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));

                    var far = new FarBodies();
                    var view = root.AddComponent<RemotePlayerView>();
                    view.Initialize(7, "character", "Ada", "player.default", 30, 15, null, null, far);
                    far.SetEye(Vector3.zero, 100f);

                    void At(float meters, bool casts, string message)
                    {
                        root.transform.position = new Vector3(0f, 0f, meters);
                        view.CheckShadow(100f);
                        Assert.That(view.CastsShadow, Is.EqualTo(casts), message);
                        Assert.That(appearance.CastsShadow, Is.EqualTo(casts), message);
                        Assert.That(renderer.shadowCastingMode, Is.EqualTo(casts ? ShadowCastingMode.On : ShadowCastingMode.Off), message);
                    }

                    At(10f, true, "a near body casts its shadow");
                    At(50f, false, "a far body casts none");
                    At(42f, false, "a body at the boundary stays without");
                    At(39f, true, "a body that came near casts it again");
                    At(43f, true, "a body at the boundary keeps it");
                    Assert.That(ManagedAllocations.Of(() => view.CheckShadow(100f)), Is.Zero);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
