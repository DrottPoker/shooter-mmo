using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    public sealed class PlayerBodyMeshesTests
    {
        private const string BodyPrefabPath = "Assets/Prefabs/Player/HumanMaleBody.prefab";

        [Test]
        public void ACombinedBodySkinsEveryVertexWhereItsPartsDo()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var body = InstantiateBody(scene);
                    var parts = body.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
                    Assert.That(parts.Length, Is.GreaterThan(1));
                    Pose(body);

                    // The parts as they skin in this pose, in the body's space.
                    var expected = new List<Vector3>();
                    var baked = new Mesh();
                    foreach (var part in parts)
                    {
                        part.BakeMesh(baked, true);
                        var toBody = body.transform.worldToLocalMatrix * part.transform.localToWorldMatrix;
                        expected.AddRange(baked.vertices.Select(vertex => toBody.MultiplyPoint3x4(vertex)));
                    }

                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    Pose(body);
                    var combined = appearance.CombinedRenderer;
                    Assert.That(combined, Is.Not.Null);
                    Assert.That(parts.All(part => !part.enabled), Is.True);
                    combined.BakeMesh(baked, true);
                    var toBodyCombined = body.transform.worldToLocalMatrix * combined.transform.localToWorldMatrix;
                    var actual = baked.vertices.Select(vertex => toBodyCombined.MultiplyPoint3x4(vertex)).ToArray();
                    Object.DestroyImmediate(baked);

                    Assert.That(actual.Length, Is.EqualTo(expected.Count));
                    var largest = 0f;
                    for (var index = 0; index < actual.Length; index++)
                    {
                        largest = Mathf.Max(largest, (actual[index] - expected[index]).magnitude);
                    }

                    Assert.That(largest, Is.LessThan(0.0005f), "largest vertex difference in meters");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ACombinedBodyKeepsThePaintUvsOfItsParts()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var body = InstantiateBody(scene);
                    var parts = body.GetComponentsInChildren<SkinnedMeshRenderer>().ToArray();
                    // The skin's parts carry the paint UVs, their second UV map; the eyes, teeth,
                    // and tongue have none and take zero.
                    var expected = new List<Vector2>();
                    var painted = 0;
                    foreach (var part in parts)
                    {
                        var uvs = new List<Vector2>();
                        part.sharedMesh.GetUVs(1, uvs);
                        painted += uvs.Count > 0 ? 1 : 0;
                        expected.AddRange(uvs.Count > 0 ? uvs : Enumerable.Repeat(Vector2.zero, part.sharedMesh.vertexCount));
                    }

                    Assert.That(painted, Is.GreaterThanOrEqualTo(10), "body parts with paint UVs");
                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var actual = new List<Vector2>();
                    appearance.CombinedRenderer.sharedMesh.GetUVs(1, actual);
                    Assert.That(actual, Is.EqualTo(expected));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ACombinedBodyIsBoundedAroundEveryPart()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var body = InstantiateBody(scene);
                    var parts = body.GetComponentsInChildren<SkinnedMeshRenderer>()
                        .ToDictionary(part => part.name, part => part.bounds);
                    var appearance = body.GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var combined = appearance.CombinedRenderer.bounds;
                    combined.Expand(0.001f);
                    foreach (var part in parts)
                    {
                        Assert.That(combined.Contains(part.Value.min), Is.True, part.Key);
                        Assert.That(combined.Contains(part.Value.max), Is.True, part.Key);
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void BodiesWithTheSameVisiblePartsShareOneMeshWithOneSubmeshPerMaterial()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var first = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var second = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var materials = first.GetComponentsInChildren<SkinnedMeshRenderer>()
                        .SelectMany(part => part.sharedMaterials)
                        .Distinct()
                        .ToArray();
                    first.CombineParts(meshes);
                    second.CombineParts(meshes);

                    Assert.That(meshes.Count, Is.EqualTo(1));
                    Assert.That(first.CombinedRenderer.sharedMesh, Is.SameAs(second.CombinedRenderer.sharedMesh));
                    Assert.That(first.CombinedRenderer.sharedMesh.subMeshCount, Is.EqualTo(materials.Length));
                    Assert.That(first.CombinedRenderer.sharedMaterials, Is.EquivalentTo(materials));
                    // Each body skins the shared mesh with its own bones.
                    Assert.That(
                        first.CombinedRenderer.bones.All(bone => bone.IsChildOf(first.transform)),
                        Is.True);
                    Assert.That(
                        second.CombinedRenderer.bones.All(bone => bone.IsChildOf(second.transform)),
                        Is.True);

                    // Combining again changes nothing.
                    first.CombineParts(meshes);
                    Assert.That(
                        first.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                            .Count(part => part.name == PlayerBodyAppearance.CombinedRendererName),
                        Is.EqualTo(1));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ATintedCombinedBodyTintsOnlyItsSkin()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var tints = new PlayerBodyMaterials())
                {
                    var appearance = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    appearance.CombineParts(meshes);
                    var own = appearance.CombinedRenderer.sharedMaterials;
                    // Untinted, the body draws its look, with the paint of its underwear on the skin.
                    appearance.ApplyTint(false, PlayerBodyColors.Player, tints);
                    var untinted = appearance.CombinedRenderer.sharedMaterials;

                    appearance.ApplyTint(true, PlayerBodyColors.Player, tints);
                    var tinted = appearance.CombinedRenderer.sharedMaterials;
                    for (var index = 0; index < own.Length; index++)
                    {
                        var skin = appearance.SkinMaterials.Contains(own[index]);
                        Assert.That(tinted[index] != untinted[index], Is.EqualTo(skin), own[index].name);
                    }

                    appearance.ApplyTint(false, PlayerBodyColors.Player, tints);
                    Assert.That(appearance.CombinedRenderer.sharedMaterials, Is.EqualTo(untinted));
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // A body that comes into view waits for a build on a worker thread, which must make the
        // mesh the main thread would: every vertex, weight, triangle, and bound to the bit.
        [Test]
        public void AWorkerThreadBuildsTheSameMeshAsTheMainThread()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var appearance = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                var catalog = appearance.Catalog;
                // Hair pressed flat under the hood, a jerkin over the body, a rigid sword, and a bow
                // with bones of its own.
                var look = BodyLook.Default
                    .WithGarment("DarkBrownHair", true)
                    .WithGarment("WoolHoodUp", true)
                    .WithGarment("LeatherJerkin", true)
                    .WithAttachment("IronSword", true)
                    .WithAttachment("TrainingShortbow", true);
                var worn = PlayerBodyAppearance.WornBy(catalog, look);
                var held = PlayerBodyAppearance.HeldBy(catalog, look);
                Assert.That(worn.Count, Is.EqualTo(3));
                Assert.That(held.Count, Is.EqualTo(2));
                Assert.That(held.Any(attachment => attachment.IsSkinned), Is.True);
                var parts = PlayerBodyMeshes.PartsOf(appearance);
                var main = PlayerBodyMeshes.Build(parts, appearance.transform, catalog, worn, held);
                var worker = PlayerBodyMeshes.BuildOnWorkerThread(parts, appearance.transform, catalog, worn, held);
                try
                {
                    AssertSameMesh(main, worker);
                }
                finally
                {
                    Object.DestroyImmediate(main.Mesh);
                    Object.DestroyImmediate(worker.Mesh);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // A build asked for runs on a worker thread while the main thread goes on, and its mesh
        // keeps no copy of its data once uploaded when the set says so.
        [Test]
        public void AWantedCombinationIsBuiltOffTheMainThread()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath).GetComponent<PlayerBodyAppearance>();
            using (var meshes = new PlayerBodyMeshes(true))
            {
                var look = BodyLook.Default.WithGarment("LeatherJerkin", true);
                var wanted = meshes.Want(prefab, look);
                Assert.That(wanted.IsReady, Is.False);
                Assert.That(meshes.Pending, Is.EqualTo(1));
                Assert.That(meshes.Want(prefab, look), Is.SameAs(wanted), "a combination is asked for once");
                var deadline = System.DateTime.UtcNow.AddSeconds(30);
                while (!wanted.IsReady && System.DateTime.UtcNow < deadline)
                {
                    meshes.Update();
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(wanted.IsReady, Is.True);
                Assert.That(wanted.Mesh, Is.Not.Null);
                Assert.That(wanted.Mesh.isReadable, Is.False);
                Assert.That(meshes.Pending, Is.EqualTo(0));
                Assert.That(meshes.Count, Is.EqualTo(1));
            }
        }

        // What the builds read of the source meshes stays while builds come and goes a grace time
        // after the last one, also while the meshes built from it are still shown; the next build
        // reads its sources again.
        [Test]
        public void TheSourceDataOfTheBuildsGoesAGraceTimeAfterTheLastBuild()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var first = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    first.CombineParts(meshes);
                    var now = Time.realtimeSinceStartup;
                    Assert.That(meshes.SourceCount, Is.GreaterThan(0));

                    meshes.ReleaseBuildData(now);
                    Assert.That(meshes.SourceCount, Is.GreaterThan(0), "it stays its grace time");

                    meshes.ReleaseBuildData(now + PlayerBodyMeshes.GraceSeconds + 1f);
                    Assert.That(meshes.SourceCount, Is.EqualTo(0));
                    Assert.That(first.CombinedRenderer.sharedMesh, Is.Not.Null, "the built mesh stays");

                    first.ReleaseShared();
                    meshes.ReleaseUnused();
                    Assert.That(meshes.Count, Is.EqualTo(0));
                    var second = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    second.CombineParts(meshes);
                    Assert.That(meshes.SourceCount, Is.GreaterThan(0), "the next build read them again");
                    Assert.That(second.CombinedRenderer.sharedMesh, Is.Not.Null);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // A mesh no body uses stays its grace time, at most UnusedCap of them, and then goes; a
        // body that takes it again keeps it.
        [Test]
        public void AMeshNobodyUsesGoesAfterItsGraceTime()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var first = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var second = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    first.CombineParts(meshes);
                    second.CombineParts(meshes);
                    var mesh = first.CombinedRenderer.sharedMesh;
                    var now = Time.realtimeSinceStartup;
                    var later = Time.frameCount + PlayerBodyMeshes.WantedFrames + 1;

                    first.ReleaseShared();
                    meshes.Trim(now + PlayerBodyMeshes.GraceSeconds + 1f, later);
                    Assert.That(meshes.Count, Is.EqualTo(1), "the second body still shows it");

                    second.ReleaseShared();
                    meshes.Trim(now, later);
                    Assert.That(meshes.Count, Is.EqualTo(1), "it stays its grace time");
                    meshes.Trim(now + PlayerBodyMeshes.GraceSeconds + 1f, later);
                    Assert.That(meshes.Count, Is.EqualTo(0));
                    Assert.That(mesh == null, Is.True, "the mesh is destroyed");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void AtMostUnusedCapMeshesNobodyUsesStay()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var appearance = InstantiateBody(scene).GetComponent<PlayerBodyAppearance>();
                    var catalog = appearance.Catalog;
                    var parts = PlayerBodyMeshes.PartsOf(appearance);
                    var garments = catalog.Garments.Where(garment => garment != null && garment.Model != null).ToArray();
                    // Every garment alone, then pairs of them, until there are two more combinations
                    // than the cap.
                    var combinations = garments.Select(garment => new[] { garment })
                        .Concat(garments.SelectMany((first, index) => garments.Skip(index + 1).Select(second => new[] { first, second })));
                    var built = new List<Mesh>();
                    foreach (var combination in combinations)
                    {
                        var mesh = meshes.For(parts, appearance.transform, catalog, combination).Mesh;
                        if (!built.Contains(mesh))
                        {
                            built.Add(mesh);
                        }

                        if (built.Count == PlayerBodyMeshes.UnusedCap + 2)
                        {
                            break;
                        }
                    }

                    Assert.That(built.Count, Is.EqualTo(PlayerBodyMeshes.UnusedCap + 2));
                    Assert.That(meshes.Count, Is.EqualTo(built.Count));

                    // While a body still asks for them they stay.
                    meshes.Trim(Time.realtimeSinceStartup, Time.frameCount);
                    Assert.That(meshes.Count, Is.EqualTo(built.Count));

                    meshes.Trim(Time.realtimeSinceStartup, Time.frameCount + PlayerBodyMeshes.WantedFrames + 1);
                    Assert.That(meshes.Count, Is.EqualTo(PlayerBodyMeshes.UnusedCap));
                    Assert.That(built[0] == null && built[1] == null, Is.True, "the oldest go first");
                    Assert.That(built[2] != null, Is.True);
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // A material copy no body or corpse shows goes after the grace time.
        [Test]
        public void AMaterialCopyNobodyShowsGoesAfterItsGraceTime()
        {
            var source = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                using (var materials = new PlayerBodyMaterials())
                {
                    var copy = materials.For(source, Color.red);
                    Assert.That(copy, Is.Not.SameAs(source));
                    materials.AddUser(copy);
                    materials.AddUser(source);
                    var now = Time.realtimeSinceStartup;
                    materials.Trim(now + PlayerBodyMeshes.GraceSeconds + 1f);
                    Assert.That(materials.Count, Is.EqualTo(1), "a copy in use stays");

                    materials.Release(copy);
                    materials.Trim(now);
                    Assert.That(materials.Count, Is.EqualTo(1), "it stays its grace time");
                    Assert.That(materials.For(source, Color.red), Is.SameAs(copy));
                    materials.Trim(now + PlayerBodyMeshes.GraceSeconds + 1f);
                    Assert.That(materials.Count, Is.EqualTo(0));
                    Assert.That(copy == null, Is.True);
                }
            }
            finally
            {
                Object.DestroyImmediate(source);
            }
        }

        private static void AssertSameMesh(PlayerBodyMeshes.Combined expected, PlayerBodyMeshes.Combined actual)
        {
            var a = expected.Mesh;
            var b = actual.Mesh;
            Assert.That(b.vertexCount, Is.EqualTo(a.vertexCount));
            AssertSameBits(a.vertices.SelectMany(Floats), b.vertices.SelectMany(Floats), "positions");
            AssertSameBits(a.normals.SelectMany(Floats), b.normals.SelectMany(Floats), "normals");
            AssertSameBits(a.tangents.SelectMany(Floats), b.tangents.SelectMany(Floats), "tangents");
            var uvs = new List<Vector2>();
            var otherUvs = new List<Vector2>();
            for (var channel = 0; channel < 2; channel++)
            {
                a.GetUVs(channel, uvs);
                b.GetUVs(channel, otherUvs);
                AssertSameBits(uvs.SelectMany(uv => new[] { uv.x, uv.y }), otherUvs.SelectMany(uv => new[] { uv.x, uv.y }), "uv" + channel);
            }

            Assert.That(b.GetBonesPerVertex().ToArray(), Is.EqualTo(a.GetBonesPerVertex().ToArray()));
            var weights = a.GetAllBoneWeights().ToArray();
            var otherWeights = b.GetAllBoneWeights().ToArray();
            Assert.That(otherWeights.Select(weight => weight.boneIndex), Is.EqualTo(weights.Select(weight => weight.boneIndex)));
            AssertSameBits(weights.Select(weight => weight.weight), otherWeights.Select(weight => weight.weight), "weights");
            AssertSameBits(a.bindposes.SelectMany(Floats), b.bindposes.SelectMany(Floats), "bind poses");
            Assert.That(b.subMeshCount, Is.EqualTo(a.subMeshCount));
            for (var submesh = 0; submesh < a.subMeshCount; submesh++)
            {
                Assert.That(b.GetTriangles(submesh), Is.EqualTo(a.GetTriangles(submesh)), "submesh " + submesh);
            }

            Assert.That(b.indexFormat, Is.EqualTo(a.indexFormat));
            Assert.That(actual.Materials, Is.EqualTo(expected.Materials));
            Assert.That(actual.BoneNames, Is.EqualTo(expected.BoneNames));
            Assert.That(actual.RootBoneName, Is.EqualTo(expected.RootBoneName));
            AssertSameBits(Floats(expected.LocalBounds.center).Concat(Floats(expected.LocalBounds.size)),
                Floats(actual.LocalBounds.center).Concat(Floats(actual.LocalBounds.size)), "bounds");
        }

        private static IEnumerable<float> Floats(Vector3 value)
        {
            return new[] { value.x, value.y, value.z };
        }

        private static IEnumerable<float> Floats(Vector4 value)
        {
            return new[] { value.x, value.y, value.z, value.w };
        }

        private static IEnumerable<float> Floats(Matrix4x4 value)
        {
            return Enumerable.Range(0, 16).Select(index => value[index]);
        }

        private static void AssertSameBits(IEnumerable<float> expected, IEnumerable<float> actual, string what)
        {
            var expectedBits = expected.Select(System.BitConverter.SingleToInt32Bits).ToArray();
            var actualBits = actual.Select(System.BitConverter.SingleToInt32Bits).ToArray();
            Assert.That(actualBits.Length, Is.EqualTo(expectedBits.Length), what);
            Assert.That(actualBits, Is.EqualTo(expectedBits), what);
        }

        private static GameObject InstantiateBody(UnityEngine.SceneManagement.Scene scene)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefabPath);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            body.transform.SetPositionAndRotation(new Vector3(3f, 0.5f, -2f), Quaternion.Euler(0f, 40f, 0f));
            return body;
        }

        // A jog pose, so every bone is away from its rest pose.
        private static void Pose(GameObject body)
        {
            var animator = body.GetComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.SetBool(HumanLocomotion.MovingParameter, true);
            animator.SetFloat(HumanLocomotion.SpeedParameter, HumanLocomotion.JogSpeed);
            for (var frame = 0; frame < 12; frame++)
            {
                animator.Update(1f / 30f);
            }
        }
    }
}
