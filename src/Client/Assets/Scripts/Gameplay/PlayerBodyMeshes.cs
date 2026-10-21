using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Combined meshes of player bodies. A body is made of skinned parts, one per equipment
    // zone plus the eyes, teeth, and tongue, so equipment can hide a zone. Drawn as parts, a
    // crowd paid for every part of every body; combined, a body is one skinned renderer with
    // one submesh per material. Every body with the same visible parts of the same model shares
    // one mesh. The owner destroys the meshes.
    public sealed class PlayerBodyMeshes : IDisposable
    {
        private readonly Dictionary<string, Combined> meshes = new Dictionary<string, Combined>();

        public int Count
        {
            get { return meshes.Count; }
        }

        // The combined mesh of these parts, built once and then shared. The parts must be
        // bound to one skeleton below the root, with readable meshes.
        public Combined For(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            var key = KeyOf(parts);
            if (!meshes.TryGetValue(key, out var combined) || combined.Mesh == null)
            {
                combined = Build(parts, root);
                meshes[key] = combined;
            }

            return combined;
        }

        public void Dispose()
        {
            foreach (var combined in meshes.Values)
            {
                if (combined.Mesh == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(combined.Mesh);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(combined.Mesh);
                }
            }

            meshes.Clear();
        }

        // Every part's mesh in the root's space, the bones of all parts in one list, and the
        // triangles of each material in one submesh, in the order the materials first appear.
        public static Combined Build(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            if (parts == null || parts.Count == 0)
            {
                throw new ArgumentException("A combined body needs at least one part.", nameof(parts));
            }

            var bones = new List<Transform>();
            var boneIndex = new Dictionary<Transform, int>();
            var bindposes = new List<Matrix4x4>();
            var materials = new List<Material>();
            var triangles = new List<List<int>>();
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvs = new List<Vector2>();
            var bonesPerVertex = new List<byte>();
            var weights = new List<BoneWeight1>();
            var partVertices = new List<Vector3>();
            var partNormals = new List<Vector3>();
            var partTangents = new List<Vector4>();
            var partUvs = new List<Vector2>();
            var partBindposes = new List<Matrix4x4>();
            var partTriangles = new List<int>();
            var bounds = new Bounds();
            var hasBounds = false;
            var rootBone = parts[0].rootBone;

            foreach (var part in parts)
            {
                var mesh = part.sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    throw new InvalidOperationException("Body part '" + part.name + "' has no readable mesh.");
                }

                // The part's vertices in the root's space; a rigid part such as an eye has a
                // pivot of its own.
                var toRoot = root.worldToLocalMatrix * part.transform.localToWorldMatrix;
                var fromRoot = toRoot.inverse;
                var normalMatrix = fromRoot.transpose;
                var first = positions.Count;
                mesh.GetVertices(partVertices);
                mesh.GetNormals(partNormals);
                mesh.GetTangents(partTangents);
                mesh.GetUVs(0, partUvs);
                for (var index = 0; index < partVertices.Count; index++)
                {
                    positions.Add(toRoot.MultiplyPoint3x4(partVertices[index]));
                    normals.Add(index < partNormals.Count
                        ? normalMatrix.MultiplyVector(partNormals[index]).normalized
                        : Vector3.up);
                    if (index < partTangents.Count)
                    {
                        var tangent = toRoot.MultiplyVector(partTangents[index]).normalized;
                        tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, partTangents[index].w));
                    }
                    else
                    {
                        tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    }

                    uvs.Add(index < partUvs.Count ? partUvs[index] : Vector2.zero);
                }

                // A bone keeps one bind pose for every part: the part's bind pose in the root's space.
                mesh.GetBindposes(partBindposes);
                var partBones = part.bones;
                var remap = new int[partBones.Length];
                for (var index = 0; index < partBones.Length; index++)
                {
                    var bone = partBones[index];
                    if (!boneIndex.TryGetValue(bone, out var unified))
                    {
                        unified = bones.Count;
                        boneIndex.Add(bone, unified);
                        bones.Add(bone);
                        bindposes.Add(partBindposes[index] * fromRoot);
                    }

                    remap[index] = unified;
                }

                var partBonesPerVertex = mesh.GetBonesPerVertex();
                var partWeights = mesh.GetAllBoneWeights();
                for (var index = 0; index < partBonesPerVertex.Length; index++)
                {
                    bonesPerVertex.Add(partBonesPerVertex[index]);
                }

                for (var index = 0; index < partWeights.Length; index++)
                {
                    var weight = partWeights[index];
                    weight.boneIndex = remap[weight.boneIndex];
                    weights.Add(weight);
                }

                var partMaterials = part.sharedMaterials;
                for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    var material = submesh < partMaterials.Length ? partMaterials[submesh] : null;
                    var slot = materials.IndexOf(material);
                    if (slot < 0)
                    {
                        slot = materials.Count;
                        materials.Add(material);
                        triangles.Add(new List<int>());
                    }

                    mesh.GetTriangles(partTriangles, submesh);
                    foreach (var vertex in partTriangles)
                    {
                        triangles[slot].Add(first + vertex);
                    }
                }

                // The bounds of the parts, in the space of their common root bone.
                if (part.rootBone == rootBone)
                {
                    if (hasBounds)
                    {
                        bounds.Encapsulate(part.localBounds);
                    }
                    else
                    {
                        bounds = part.localBounds;
                        hasBounds = true;
                    }
                }
            }

            var combinedMesh = new Mesh
            {
                name = parts[0].sharedMesh.name + " combined",
                indexFormat = positions.Count > ushort.MaxValue
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            combinedMesh.SetVertices(positions);
            combinedMesh.SetNormals(normals);
            combinedMesh.SetTangents(tangents);
            combinedMesh.SetUVs(0, uvs);
            using (var perVertex = new NativeArray<byte>(bonesPerVertex.ToArray(), Allocator.Temp))
            using (var allWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
            {
                combinedMesh.SetBoneWeights(perVertex, allWeights);
            }

            combinedMesh.bindposes = bindposes.ToArray();
            combinedMesh.subMeshCount = triangles.Count;
            for (var submesh = 0; submesh < triangles.Count; submesh++)
            {
                combinedMesh.SetTriangles(triangles[submesh], submesh, false);
            }

            combinedMesh.RecalculateBounds();
            var boneNames = new string[bones.Count];
            for (var index = 0; index < bones.Count; index++)
            {
                boneNames[index] = bones[index].name;
            }

            return new Combined(
                combinedMesh,
                boneNames,
                materials.ToArray(),
                rootBone != null ? rootBone.name : null,
                hasBounds ? bounds : combinedMesh.bounds);
        }

        private static string KeyOf(IReadOnlyList<SkinnedMeshRenderer> parts)
        {
            var key = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                key.Append(part.sharedMesh != null ? part.sharedMesh.GetEntityId().ToString() : "-").Append('/');
                foreach (var material in part.sharedMaterials)
                {
                    key.Append(material != null ? material.GetEntityId().ToString() : "-").Append(',');
                }

                key.Append(';');
            }

            return key.ToString();
        }

        public sealed class Combined
        {
            public Combined(Mesh mesh, string[] boneNames, Material[] materials, string rootBoneName, Bounds localBounds)
            {
                Mesh = mesh;
                BoneNames = boneNames;
                Materials = materials;
                RootBoneName = rootBoneName;
                LocalBounds = localBounds;
            }

            public Mesh Mesh { get; }

            // The bones of the mesh by name, so every body binds the shared mesh to its own bones.
            public IReadOnlyList<string> BoneNames { get; }

            public IReadOnlyList<Material> Materials { get; }

            public string RootBoneName { get; }

            public Bounds LocalBounds { get; }
        }
    }
}
