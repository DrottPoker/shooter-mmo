using System.Linq;
using System.Text.RegularExpressions;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace Mmorpg.Client.Tests.EditMode
{
    // The content of looks loads on demand, counts its users, and goes a grace time after its last
    // user, with the meshes and material copies made of it; see docs/UNITY_CLIENT_ARCHITECTURE.md,
    // Body Content.
    public sealed class PlayerBodyContentTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";
        private const string Jerkin = "Tests/Jerkin.asset";
        private const string Trousers = "Tests/Trousers.asset";

        private BodyPieceContent jerkin;
        private BodyPieceContent trousers;
        private GameObject model;
        private BodyAppearanceCatalog catalog;

        [SetUp]
        public void CreateCatalog()
        {
            model = new GameObject("GarmentModel");
            jerkin = ScriptableObject.CreateInstance<BodyPieceContent>();
            jerkin.Configure(model, null, null);
            trousers = ScriptableObject.CreateInstance<BodyPieceContent>();
            trousers.Configure(model, null, null);
            catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
            catalog.Configure(new BodyAppearanceCatalog.Slot[0], null, null, new[]
            {
                Garment("WoolTrousers", Trousers, "legs", 100),
                Garment("LeatherJerkin", Jerkin, "body", 200),
                Garment("MissingCoat", "Tests/Missing.asset", "back", 300),
            });
        }

        [TearDown]
        public void DestroyCatalog()
        {
            Object.DestroyImmediate(jerkin);
            Object.DestroyImmediate(trousers);
            Object.DestroyImmediate(model);
            Object.DestroyImmediate(catalog);
        }

        [Test]
        public void ALookIsReadyOnceItsContentLoadedAndItsUsersKeepIt()
        {
            var source = new TestContentSource { Immediate = false }.With(Jerkin, jerkin).With(Trousers, trousers);
            using (var content = source.Content())
            {
                var look = BodyLook.Default.WithGarment("LeatherJerkin", true);
                var needs = PlayerBodyContent.NeedsOf(catalog, look);
                Assert.That(needs.Addresses, Is.EqualTo(new[] { Jerkin }));
                Assert.That(content.Want(needs), Is.False, "nothing is ready before its load is done");
                content.Update();
                Assert.That(content.Want(needs), Is.False);
                Assert.That(content.PieceOf(Jerkin), Is.Null);

                source.CompleteAll();
                content.Update();
                Assert.That(content.Want(needs), Is.True);
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin));
                Assert.That(content.GarmentCount, Is.EqualTo(1));

                // A user keeps it beyond its grace time; without users it goes after it.
                content.AddUser(needs);
                var now = Time.realtimeSinceStartup;
                var later = Time.frameCount + PlayerBodyContent.WantedFrames + 1;
                content.Trim(now + PlayerBodyContent.GraceSeconds + 1f, later);
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin));
                content.Release(needs);
                content.Trim(now, later);
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin), "it stays its grace time");
                content.Trim(now + PlayerBodyContent.GraceSeconds + 1f, later);
                Assert.That(content.PieceOf(Jerkin), Is.Null);
                Assert.That(content.GarmentCount, Is.Zero);
                Assert.That(source.Loads.Single().Released, Is.True, "its load is released");
            }
        }

        [Test]
        public void ALookAskedForStaysWhileItIsAskedFor()
        {
            var source = new TestContentSource().With(Jerkin, jerkin);
            using (var content = source.Content())
            {
                var needs = PlayerBodyContent.NeedsOf(catalog, BodyLook.Default.WithGarment("LeatherJerkin", true));
                content.Want(needs);
                content.Update();
                // No user, but asked for this frame: a body waits for it.
                content.Trim(Time.realtimeSinceStartup + PlayerBodyContent.GraceSeconds + 1f, Time.frameCount);
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin));
                content.Trim(Time.realtimeSinceStartup + PlayerBodyContent.GraceSeconds + 1f, Time.frameCount + PlayerBodyContent.WantedFrames + 1);
                Assert.That(content.PieceOf(Jerkin), Is.Null);
            }
        }

        [Test]
        public void LooksThatShareAGarmentShareOneLoadAndItsUsers()
        {
            var source = new TestContentSource().With(Jerkin, jerkin).With(Trousers, trousers);
            using (var content = source.Content())
            {
                var alone = PlayerBodyContent.NeedsOf(catalog, BodyLook.Default.WithGarment("LeatherJerkin", true));
                var both = PlayerBodyContent.NeedsOf(catalog, BodyLook.Default.WithGarment("LeatherJerkin", true).WithGarment("WoolTrousers", true));
                Assert.That(both.Addresses, Is.EquivalentTo(new[] { Jerkin, Trousers }));
                content.AddUser(alone);
                content.AddUser(both);
                content.Update();
                Assert.That(source.Loads.Count(load => load.Address == Jerkin), Is.EqualTo(1), "one load for both looks");

                var far = Time.realtimeSinceStartup + PlayerBodyContent.GraceSeconds + 1f;
                var later = Time.frameCount + PlayerBodyContent.WantedFrames + 1;
                content.Release(both);
                content.Trim(far, later);
                Assert.That(content.PieceOf(Trousers), Is.Null, "the trousers had one user");
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin), "the other look still shows the jerkin");
                content.Release(alone);
                content.Trim(far, later);
                Assert.That(content.PieceOf(Jerkin), Is.Null);
            }
        }

        [Test]
        public void ASecondRequestForAnEntryThatLoadsStartsNoLoad()
        {
            var source = new TestContentSource { Immediate = false }.With(Jerkin, jerkin);
            using (var content = source.Content())
            {
                var look = BodyLook.Default.WithGarment("LeatherJerkin", true);
                var first = PlayerBodyContent.NeedsOf(catalog, look);
                var second = PlayerBodyContent.NeedsOf(catalog, look);
                Assert.That(content.Want(first), Is.False);
                content.Update();
                Assert.That(content.Want(second), Is.False);
                content.AddUser(second);
                Assert.That(source.Loads.Count, Is.EqualTo(1));

                source.CompleteAll();
                content.Update();
                Assert.That(content.Want(first) && content.Want(second), Is.True);
                Assert.That(source.Loads.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void AReleaseWhileALoadRunsReleasesItAndTakesNothing()
        {
            var source = new TestContentSource { Immediate = false }.With(Jerkin, jerkin);
            using (var content = source.Content())
            {
                var needs = PlayerBodyContent.NeedsOf(catalog, BodyLook.Default.WithGarment("LeatherJerkin", true));
                Assert.That(content.Want(needs), Is.False);
                content.Update();
                Assert.That(content.Loading, Is.EqualTo(1));

                // Nobody asks for it any more, as when the player left before its look loaded.
                content.Trim(Time.realtimeSinceStartup + PlayerBodyContent.GraceSeconds + 1f, Time.frameCount + PlayerBodyContent.WantedFrames + 1);
                Assert.That(content.Loading, Is.Zero);
                Assert.That(source.Loads[0].Released, Is.True, "the running load is released");
                source.CompleteAll();
                content.Update();
                Assert.That(content.PieceOf(Jerkin), Is.Null, "a load released in flight is never taken");

                // Asked for again, it loads again.
                Assert.That(content.Want(needs), Is.False);
                Assert.That(source.Loads.Count, Is.EqualTo(2));
                source.CompleteAll();
                content.Update();
                Assert.That(content.Want(needs), Is.True);
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin));
            }
        }

        [Test]
        public void AFailedLoadIsReportedOnceAndTheLookIsReadyWithoutIt()
        {
            var image = new Texture2D(2, 2);
            var source = new TestContentSource().With(Jerkin, jerkin).With(Trousers, image);
            using (var content = source.Content())
            {
                LogAssert.Expect(LogType.Error, new Regex(@"\[BODY CONTENT\] Tests/Missing\.asset cannot be loaded"));
                LogAssert.Expect(LogType.Error, new Regex(@"\[BODY CONTENT\] Tests/Trousers\.asset cannot be loaded"));
                var look = BodyLook.Default.WithGarment("LeatherJerkin", true).WithGarment("WoolTrousers", true).WithGarment("MissingCoat", true);
                var needs = PlayerBodyContent.NeedsOf(catalog, look);
                content.Want(needs);
                content.Update();
                Assert.That(content.Want(needs), Is.True, "a body shows without the pieces that failed");
                Assert.That(content.PieceOf(Jerkin), Is.SameAs(jerkin));
                Assert.That(content.PieceOf(Trousers), Is.Null, "an entry of another type fails");
                Assert.That(content.PieceOf("Tests/Missing.asset"), Is.Null);
                Assert.That(PlayerBodyMeshes.Available(PlayerBodyAppearance.WornBy(catalog, look), content).Select(garment => garment.Name),
                    Is.EqualTo(new[] { "LeatherJerkin" }));

                // Asked for again after it went, it fails again without another report.
                content.ReleaseUnused();
                Assert.That(content.Want(PlayerBodyContent.NeedsOf(catalog, look)), Is.False);
                content.Update();
                LogAssert.NoUnexpectedReceived();
            }

            Object.DestroyImmediate(image);
        }

        // The meshes and material copies made of content go with it, before its bundle unloads; a
        // mesh a body still shows goes with its last user.
        [Test]
        public void TheCachesDropWhatTheyMadeOfContentThatGoes()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            var bodyCatalog = prefab.GetComponent<PlayerBodyAppearance>().Catalog;
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var content = EditorBodyContent.Create())
                using (var meshes = new PlayerBodyMeshes(false, content))
                using (var materials = new PlayerBodyMaterials(content))
                {
                    var body = ((GameObject)PrefabUtility.InstantiatePrefab(prefab, scene)).GetComponent<PlayerBodyAppearance>();
                    var parts = PlayerBodyMeshes.PartsOf(body);
                    var jerkinLook = BodyLook.Default.WithGarment("LeatherJerkin", true);
                    var cloakLook = BodyLook.Default.WithGarment("WoolCloak", true);
                    Assert.That(EditorBodyContent.Load(content, bodyCatalog, jerkinLook), Is.True);
                    Assert.That(EditorBodyContent.Load(content, bodyCatalog, cloakLook), Is.True);
                    var bare = meshes.For(parts, body.transform, bodyCatalog, null);
                    var dressed = meshes.For(parts, body.transform, bodyCatalog, PlayerBodyAppearance.WornBy(bodyCatalog, jerkinLook));
                    var cloaked = meshes.Acquire(parts, body.transform, bodyCatalog, PlayerBodyAppearance.WornBy(bodyCatalog, cloakLook));
                    var cloak = content.PieceOf(bodyCatalog.GarmentNamed("WoolCloak").Address);
                    var dyed = materials.For(cloak.Material, null, Color.white, null, new[] { new Color(0.17f, 0.26f, 0.39f) });
                    Assert.That(dyed, Is.Not.SameAs(cloak.Material));
                    Assert.That(meshes.Count, Is.EqualTo(3));
                    Assert.That(materials.Count, Is.EqualTo(1));

                    content.Trim(Time.realtimeSinceStartup + PlayerBodyContent.GraceSeconds + 1f, Time.frameCount + PlayerBodyContent.WantedFrames + 1);
                    Assert.That(content.GarmentCount, Is.Zero);
                    Assert.That(dressed.Mesh == null, Is.True, "the mesh of the jerkin goes with it");
                    Assert.That(dressed.Forgotten, Is.True);
                    Assert.That(bare.Mesh, Is.Not.Null, "a mesh without the content stays");
                    Assert.That(dyed == null, Is.True, "the dyed copy goes with the cloak");
                    Assert.That(materials.Count, Is.Zero);

                    // The cloaked mesh still has a user: it is forgotten and goes with that user.
                    Assert.That(cloaked.Forgotten, Is.True);
                    Assert.That(cloaked.Mesh, Is.Not.Null);
                    Assert.That(meshes.Count, Is.EqualTo(1));
                    meshes.Release(cloaked);
                    Assert.That(cloaked.Mesh == null, Is.True);

                    // The same look builds again from content that loads again, under the same key.
                    Assert.That(EditorBodyContent.Load(content, bodyCatalog, jerkinLook), Is.True);
                    var again = meshes.For(parts, body.transform, bodyCatalog, PlayerBodyAppearance.WornBy(bodyCatalog, jerkinLook));
                    Assert.That(again.Mesh, Is.Not.Null);
                    Assert.That(again.Forgotten, Is.False);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static BodyAppearanceCatalog.Garment Garment(string name, string address, string slot, int layer)
        {
            return new BodyAppearanceCatalog.Garment(name, name, address, slot, layer, new string[0], false, string.Empty, string.Empty,
                new int[0], new int[0], new int[0], new float[0]);
        }
    }
}
