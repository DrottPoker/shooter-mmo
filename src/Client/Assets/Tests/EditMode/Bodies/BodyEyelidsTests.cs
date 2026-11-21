using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The lids of the bodies: their constants are the eyelids section of the Humanoid skeleton, the
    // closed turn about the lid bone's local -X in Unity makes the margins of both lids meet on every
    // body model, open they lie several millimeters apart, and a corpse lies with closed eyes. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Eyelids.
    public sealed class BodyEyelidsTests
    {
        private static readonly string[] BodyPrefabPaths =
        {
            "Assets/Prefabs/Player/HumanMaleBody.prefab",
            "Assets/Prefabs/Player/HumanFemaleBody.prefab",
        };

        // How far apart the margins of closed lids may lie, and how far apart open ones lie at least,
        // in meters.
        private const float ClosedGap = 0.0015f;
        private const float OpenGap = 0.005f;

        // The columns across the middle half of an eye's width in which the lids' gap is measured.
        private const int Columns = 5;

        [Test]
        public void TheConstantsAreTheSkeletonsEyelidsSection()
        {
            var skeleton = ArtAssetManifests.LoadSkeleton("Humanoid");
            var eyelids = skeleton.eyelids;
            Assert.That(eyelids.upperClosedDegrees, Is.EqualTo(EyelidRig.UpperClosedDegrees));
            Assert.That(eyelids.lowerClosedDegrees, Is.EqualTo(EyelidRig.LowerClosedDegrees));
            Assert.That(eyelids.eyes.Select(eye => (eye.eye, eye.upper, eye.lower)),
                Is.EqualTo(EyelidRig.Eyes.Select(eye => (eye.Bone, eye.Upper, eye.Lower))));

            // The lids are bones of Head outside the avatar, which no clip keys.
            foreach (var eye in EyelidRig.Eyes)
            {
                foreach (var name in new[] { eye.Upper, eye.Lower })
                {
                    var bone = skeleton.bones.SingleOrDefault(candidate => candidate.name == name);
                    Assert.That(bone, Is.Not.Null, name);
                    Assert.That(bone.parent, Is.EqualTo("Head"), name);
                    Assert.That(bone.human, Is.Empty, name);
                    Assert.That(bone.deform, Is.True, name);
                }
            }
        }

        [Test]
        public void ClosedLidsMeetAndOpenLidsLieApartOnEveryBody()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    foreach (var path in BodyPrefabPaths)
                    {
                        var appearance = InstantiateBody(scene, path);
                        appearance.CombineParts(meshes);
                        var lids = appearance.Eyelids;
                        Assert.That(lids, Is.Not.Null, path);
                        Assert.That(lids.Share, Is.EqualTo(0f), path);
                        var baked = new Mesh();
                        try
                        {
                            appearance.CombinedRenderer.BakeMesh(baked, true);
                            var open = LidGaps(appearance.CombinedRenderer, baked.vertices);
                            lids.Show(1f);
                            appearance.CombinedRenderer.BakeMesh(baked, true);
                            var closed = LidGaps(appearance.CombinedRenderer, baked.vertices);
                            lids.Show(0f);
                            appearance.CombinedRenderer.BakeMesh(baked, true);
                            var openAgain = LidGaps(appearance.CombinedRenderer, baked.vertices);
                            TestContext.WriteLine(path + ": lid gaps open " + Millimeters(open) + ", closed " + Millimeters(closed));
                            for (var eye = 0; eye < EyelidRig.Eyes.Count; eye++)
                            {
                                var name = path + " " + EyelidRig.Eyes[eye].Bone;
                                Assert.That(open[eye], Is.GreaterThan(OpenGap), name + " open");
                                Assert.That(closed[eye], Is.LessThan(ClosedGap), name + " closed");
                                Assert.That(openAgain[eye], Is.EqualTo(open[eye]).Within(1e-5f), name + " open again");
                            }
                        }
                        finally
                        {
                            Object.DestroyImmediate(baked);
                        }
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ASharePutsEveryLidAThatFarIntoItsClosedTurn()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                {
                    var appearance = InstantiateBody(scene, BodyPrefabPaths[0]);
                    appearance.CombineParts(meshes);
                    var lids = appearance.Eyelids;
                    var rest = lids.Bones.Select(bone => bone.localRotation).ToArray();
                    var degrees = new[]
                    {
                        EyelidRig.UpperClosedDegrees, EyelidRig.LowerClosedDegrees,
                        EyelidRig.UpperClosedDegrees, EyelidRig.LowerClosedDegrees,
                    };
                    Assert.That(lids.Bones.Select(bone => bone.name), Is.EqualTo(EyelidRig.Eyes.SelectMany(eye => new[] { eye.Upper, eye.Lower })));
                    lids.Show(0.5f);
                    Assert.That(lids.Share, Is.EqualTo(0.5f));
                    for (var index = 0; index < rest.Length; index++)
                    {
                        var expected = rest[index] * Quaternion.AngleAxis(0.5f * degrees[index], Vector3.left);
                        Assert.That(Quaternion.Angle(lids.Bones[index].localRotation, expected), Is.LessThan(0.01f), lids.Bones[index].name);
                        Assert.That(Quaternion.Angle(lids.Bones[index].localRotation, lids.RotationAt(index, 0.5f)), Is.LessThan(0.01f));
                    }

                    lids.Show(2f);
                    Assert.That(lids.Share, Is.EqualTo(1f), "a share is at most 1");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ABodyWithoutLidBonesHasNoLids()
        {
            var root = new GameObject("Creature");
            try
            {
                new GameObject("Head").transform.SetParent(root.transform, false);
                Assert.That(BodyEyelids.Find(root.transform, null), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ACorpseLiesWithClosedEyes()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                foreach (var path in BodyPrefabPaths)
                {
                    using (var meshes = new PlayerBodyMeshes())
                    using (var materials = new PlayerBodyMaterials())
                    using (var corpses = new PlayerCorpseBodies())
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<PlayerBodyAppearance>();
                        Assert.That(corpses.TryGet(path, prefab, BodyLook.Default, meshes, materials, out var lying, out _), Is.True, path);

                        // A living body of the same parts shares the corpse's combined mesh, whose
                        // weights tell the lids' vertices.
                        var appearance = InstantiateBody(scene, path);
                        appearance.CombineParts(meshes);
                        Assert.That(lying.vertexCount, Is.EqualTo(appearance.CombinedRenderer.sharedMesh.vertexCount), path);
                        var gaps = LidGaps(appearance.CombinedRenderer, lying.vertices);
                        TestContext.WriteLine(path + ": lid gaps of the corpse " + Millimeters(gaps));
                        for (var eye = 0; eye < EyelidRig.Eyes.Count; eye++)
                        {
                            Assert.That(gaps[eye], Is.LessThan(ClosedGap), path + " " + EyelidRig.Eyes[eye].Bone);
                        }
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static PlayerBodyAppearance InstantiateBody(UnityEngine.SceneManagement.Scene scene, string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.That(prefab, Is.Not.Null, path);
            var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            return body.GetComponent<PlayerBodyAppearance>();
        }

        // How far apart the margins of each eye's lids lie, from the vertices of the combined mesh
        // in a pose, in the order of EyelidRig.Eyes: the vertices weighted mostly to the upper lid
        // bone fall at rest into Columns columns across the middle half of the upper lid's width,
        // each column's gap is the distance from its nearest such vertex to a vertex weighted mostly
        // to the lower lid bone, and the eye's gap is its widest column's.
        private static float[] LidGaps(SkinnedMeshRenderer renderer, Vector3[] posed)
        {
            var mesh = renderer.sharedMesh;
            var rest = mesh.vertices;
            var boneNames = renderer.bones.Select(bone => bone.name).ToList();
            var gaps = new float[EyelidRig.Eyes.Count];
            for (var eye = 0; eye < gaps.Length; eye++)
            {
                var upper = MostlyWeightedTo(mesh, boneNames.IndexOf(EyelidRig.Eyes[eye].Upper));
                var lower = MostlyWeightedTo(mesh, boneNames.IndexOf(EyelidRig.Eyes[eye].Lower));
                Assert.That(upper, Is.Not.Empty, EyelidRig.Eyes[eye].Upper);
                Assert.That(lower, Is.Not.Empty, EyelidRig.Eyes[eye].Lower);
                var left = upper.Min(vertex => rest[vertex].x);
                var right = upper.Max(vertex => rest[vertex].x);
                var from = left + 0.25f * (right - left);
                var width = 0.5f * (right - left) / Columns;
                for (var column = 0; column < Columns; column++)
                {
                    var nearest = float.MaxValue;
                    foreach (var vertex in upper)
                    {
                        var across = rest[vertex].x - from - column * width;
                        if (across < 0f || across >= width)
                        {
                            continue;
                        }

                        foreach (var other in lower)
                        {
                            nearest = Mathf.Min(nearest, (posed[vertex] - posed[other]).magnitude);
                        }
                    }

                    Assert.That(nearest, Is.LessThan(float.MaxValue), "a column across the upper lid without vertices");
                    gaps[eye] = Mathf.Max(gaps[eye], nearest);
                }
            }

            return gaps;
        }

        // The vertices whose weight to the bone is more than half.
        private static List<int> MostlyWeightedTo(Mesh mesh, int bone)
        {
            var found = new List<int>();
            if (bone < 0)
            {
                return found;
            }

            var perVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var next = 0;
            for (var vertex = 0; vertex < perVertex.Length; vertex++)
            {
                var weight = 0f;
                for (var index = 0; index < perVertex[vertex]; index++)
                {
                    var boneWeight = weights[next + index];
                    if (boneWeight.boneIndex == bone)
                    {
                        weight += boneWeight.weight;
                    }
                }

                next += perVertex[vertex];
                if (weight > 0.5f)
                {
                    found.Add(vertex);
                }
            }

            return found;
        }

        private static string Millimeters(float[] gaps)
        {
            return string.Join(" and ", gaps.Select(gap => (gap * 1000f).ToString("F2") + " mm"));
        }
    }
}
