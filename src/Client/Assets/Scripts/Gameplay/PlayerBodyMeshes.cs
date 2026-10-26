using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Combined meshes of player bodies. A body is made of skinned parts, one per body zone plus
    // the eyes, teeth, and tongue, and wears garments, meshes skinned to the same skeleton. Drawn
    // as parts, a crowd paid for every part of every body; combined, a body is one skinned
    // renderer with one submesh per material. Putting a body together with garments leaves out
    // the body faces the worn garments cover, moves the pulled-in body vertices inward, leaves out
    // the garment faces whose underlay a garment of a higher layer covers, leaves out every
    // garment of a slot that a worn garment of a higher layer hides, and leaves out the hair
    // under headwear that hides it or draws it in its form under headwear that presses it flat;
    // see docs/EQUIPMENT_VISUALS_DESIGN.md. Attachments, such as a sword, are rigid meshes weighted
    // fully to their socket bone, so a body holding them is still one renderer. Every body with the
    // same visible parts of the same model, the same garments, and the same attachments shares one
    // mesh. The owner destroys the meshes.
    public sealed class PlayerBodyMeshes : IDisposable
    {
        private static readonly IReadOnlyList<BodyAppearanceCatalog.Garment> NoGarments =
            new BodyAppearanceCatalog.Garment[0];

        private static readonly IReadOnlyList<BodyAppearanceCatalog.Attachment> NoAttachments =
            new BodyAppearanceCatalog.Attachment[0];

        // How far a hand swings from where it is in the bind pose, in meters, which the bounds of a
        // body leave room for around what it holds.
        public const float AttachmentReach = 0.35f;

        private readonly Dictionary<string, Combined> meshes = new Dictionary<string, Combined>();

        public int Count
        {
            get { return meshes.Count; }
        }

        // The combined mesh of these parts, built once and then shared. The parts must be
        // bound to one skeleton below the root, with readable meshes.
        public Combined For(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            return For(parts, root, null, NoGarments);
        }

        // Also wearing the garments of the catalog, whose models are skinned to bones of the same
        // names as the parts' skeleton, and holding its attachments on their sockets below the root.
        public Combined For(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            garments = garments ?? NoGarments;
            attachments = attachments ?? NoAttachments;
            var key = KeyOf(parts, garments, attachments);
            if (!meshes.TryGetValue(key, out var combined) || combined.Mesh == null)
            {
                combined = Build(parts, root, catalog, garments, attachments);
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

        public static Combined Build(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            return Build(parts, root, null, NoGarments);
        }

        // The garments a body draws: the worn ones from the lowest layer up, without those of a slot
        // that a worn garment of a higher layer hides, such as trousers under a floor-length robe;
        // leg armor over the robe stays. Headwear that hides the hair leaves the hair out.
        public static List<BodyAppearanceCatalog.Garment> Drawn(IReadOnlyList<BodyAppearanceCatalog.Garment> garments)
        {
            var drawn = new List<BodyAppearanceCatalog.Garment>();
            foreach (var garment in garments)
            {
                if (garment == null || garment.Model == null)
                {
                    continue;
                }

                var hidden = false;
                foreach (var other in garments)
                {
                    if (other != null && other != garment
                        && ((other.Layer > garment.Layer && Contains(other.Hides, garment.Slot))
                            || (IsHair(garment) && string.Equals(other.Hair, BodyAppearanceCatalog.Garment.HideHair, StringComparison.Ordinal))))
                    {
                        hidden = true;
                        break;
                    }
                }

                if (!hidden)
                {
                    drawn.Add(garment);
                }
            }

            drawn.Sort((a, b) => a.Layer != b.Layer
                ? a.Layer.CompareTo(b.Layer)
                : string.CompareOrdinal(a.Name, b.Name));
            return drawn;
        }

        // The blend shape a drawn garment takes in full, or null: hair under headwear that presses
        // it flat takes its form under headwear.
        public static string ShapeOf(BodyAppearanceCatalog.Garment garment, IReadOnlyList<BodyAppearanceCatalog.Garment> drawn)
        {
            if (!IsHair(garment))
            {
                return null;
            }

            foreach (var other in drawn)
            {
                if (other != null && string.Equals(other.Hair, BodyAppearanceCatalog.Garment.CompressHair, StringComparison.Ordinal))
                {
                    return BodyAppearanceCatalog.Garment.UnderHeadwearShape;
                }
            }

            return null;
        }

        private static bool IsHair(BodyAppearanceCatalog.Garment garment)
        {
            return string.Equals(garment.Slot, BodyAppearanceCatalog.Garment.HairSlot, StringComparison.Ordinal);
        }

        // Every part's and every drawn garment's mesh in the root's space, the bones of all of them
        // in one list by name, and the triangles of each material in one submesh, in the order the
        // materials first appear; vertices no triangle uses are left out. Each attachment's meshes
        // lie on its socket bone as the socket stands on its parent bone in the bind pose, with its
        // root object Grip on the socket.
        public static Combined Build(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            if (parts == null || parts.Count == 0)
            {
                throw new ArgumentException("A combined body needs at least one part.", nameof(parts));
            }

            var drawn = Drawn(garments ?? NoGarments);
            var coverLayer = new Dictionary<int, int>();
            var pullIn = new Dictionary<int, float>();
            foreach (var garment in drawn)
            {
                foreach (var face in garment.Covered)
                {
                    coverLayer[face] = coverLayer.TryGetValue(face, out var layer) ? Math.Max(layer, garment.Layer) : garment.Layer;
                }

                for (var index = 0; index < garment.PullInVertices.Count && index < garment.PullInDistances.Count; index++)
                {
                    var vertex = garment.PullInVertices[index];
                    pullIn[vertex] = pullIn.TryGetValue(vertex, out var distance)
                        ? Mathf.Max(distance, garment.PullInDistances[index])
                        : garment.PullInDistances[index];
                }
            }

            var builder = new Builder(parts[0].rootBone != null ? parts[0].rootBone : root);
            foreach (var part in parts)
            {
                var map = catalog != null ? catalog.PartNamed(part.name) : null;
                builder.Add(part, root.worldToLocalMatrix * part.transform.localToWorldMatrix,
                    part.rootBone != null ? part.rootBone : part.transform, builder.RootBone,
                    // A body face stays unless a drawn garment covers it.
                    map != null && map.TriangleFaces.Count > 0 ? (Func<int, bool>)(triangle => !coverLayer.ContainsKey(map.TriangleFaces[triangle])) : null,
                    map != null && pullIn.Count > 0 ? (Func<int, float>)(vertex => pullIn.TryGetValue(map.VertexSources[vertex], out var distance) ? distance : 0f) : null);
            }

            foreach (var garment in drawn)
            {
                var renderer = garment.Model.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (renderer == null || renderer.sharedMesh == null)
                {
                    continue;
                }

                // The bones of the garment's own armature stand where the body's do, so its bounds
                // carry over from its root bone to the body's root bone of the same name.
                var garmentRootBone = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
                var bodyRootBone = FindDescendant(garment.Model.transform, builder.RootBoneName) ?? garmentRootBone;
                var underlay = garment.Underlay;
                var own = garment.Layer;
                builder.Add(renderer, garment.Model.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                    garmentRootBone, bodyRootBone,
                    // A garment face stays unless a garment of a higher layer covers the body face under it.
                    triangle => triangle >= underlay.Count || !coverLayer.TryGetValue(underlay[triangle], out var layer) || layer <= own,
                    null, garment.Material, ShapeOf(garment, drawn));
            }

            foreach (var attachment in Held(attachments ?? NoAttachments))
            {
                var socket = FindDescendant(root, attachment.Bone);
                if (socket == null || socket.parent == null || !builder.TryGetBoneToRoot(socket.parent.name, out var parentToRoot))
                {
                    continue;
                }

                // A socket is never animated, so it stands on its parent as it does in the model.
                builder.AddRigid(attachment.Model, attachment.Bone,
                    parentToRoot * Matrix4x4.TRS(socket.localPosition, socket.localRotation, socket.localScale));
            }

            return builder.Finish(parts[0].sharedMesh.name + " combined");
        }

        // The attachments with a model, one per socket bone, by name.
        public static List<BodyAppearanceCatalog.Attachment> Held(IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments)
        {
            var held = new List<BodyAppearanceCatalog.Attachment>();
            foreach (var attachment in attachments)
            {
                if (attachment != null && attachment.Model != null
                    && !held.Exists(other => string.Equals(other.Bone, attachment.Bone, StringComparison.Ordinal)))
                {
                    held.Add(attachment);
                }
            }

            held.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return held;
        }

        private static bool Contains(IReadOnlyList<string> names, string name)
        {
            foreach (var candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static Transform FindDescendant(Transform parent, string name)
        {
            foreach (var child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        // Bounds in the space of one transform, as bounds in the space of another.
        private static Bounds BoundsIn(Transform from, Bounds local, Transform to)
        {
            if (from == to)
            {
                return local;
            }

            var matrix = to.worldToLocalMatrix * from.localToWorldMatrix;
            var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
            for (var corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -local.extents.x : local.extents.x,
                    (corner & 2) == 0 ? -local.extents.y : local.extents.y,
                    (corner & 4) == 0 ? -local.extents.z : local.extents.z);
                result.Encapsulate(matrix.MultiplyPoint3x4(local.center + offset));
            }

            return result;
        }

        private static string KeyOf(IReadOnlyList<SkinnedMeshRenderer> parts, IReadOnlyList<BodyAppearanceCatalog.Garment> garments,
            IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments)
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

            var drawn = Drawn(garments);
            foreach (var garment in drawn)
            {
                key.Append('|').Append(garment.Name).Append('@').Append(garment.Model.GetEntityId().ToString())
                    .Append('/').Append(garment.Material != null ? garment.Material.GetEntityId().ToString() : "-")
                    .Append('~').Append(ShapeOf(garment, drawn) ?? string.Empty);
            }

            foreach (var attachment in Held(attachments))
            {
                key.Append('#').Append(attachment.Name).Append('@').Append(attachment.Model.GetEntityId().ToString())
                    .Append('/').Append(attachment.Bone);
            }

            return key.ToString();
        }

        // Collects the vertices, bones, and triangles of the meshes it is given.
        private sealed class Builder
        {
            private readonly Transform rootBone;
            private readonly List<string> bones = new List<string>();
            private readonly Dictionary<string, int> boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<Matrix4x4> bindposes = new List<Matrix4x4>();
            private readonly List<Material> materials = new List<Material>();
            private readonly List<List<int>> triangles = new List<List<int>>();
            private readonly List<Vector3> positions = new List<Vector3>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private readonly List<Vector4> tangents = new List<Vector4>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            // The paint UVs, the body's second UV map, which the skin shader reads its paint layers
            // through; zero on meshes without it, such as garments and the eyes.
            private readonly List<Vector2> paintUvs = new List<Vector2>();
            private readonly List<byte> bonesPerVertex = new List<byte>();
            private readonly List<int> firstWeight = new List<int>();
            private readonly List<BoneWeight1> weights = new List<BoneWeight1>();
            private readonly List<Vector3> meshVertices = new List<Vector3>();
            private readonly List<Vector3> meshNormals = new List<Vector3>();
            private readonly List<Vector4> meshTangents = new List<Vector4>();
            private readonly List<Vector2> meshUvs = new List<Vector2>();
            private readonly List<Vector2> meshPaintUvs = new List<Vector2>();
            private readonly List<Matrix4x4> meshBindposes = new List<Matrix4x4>();
            private readonly List<int> meshTriangles = new List<int>();
            private Bounds bounds;
            private bool hasBounds;

            public Builder(Transform rootBone)
            {
                this.rootBone = rootBone;
            }

            public Transform RootBone
            {
                get { return rootBone; }
            }

            public string RootBoneName
            {
                get { return rootBone.name; }
            }

            // One renderer's mesh, given in the root's space by toRoot, with its local bounds in the
            // space of boundsFrom, its root bone, carried to boundsBone, the body's root bone or the
            // bone of its armature that stands where the body's root bone does. keep says which
            // triangles stay, over the submeshes in order; pull says how far each vertex moves
            // inward; a material, such as a garment texture's, replaces the renderer's own; a blend
            // shape of the mesh, such as hair's form under headwear, is taken in full.
            public void Add(SkinnedMeshRenderer renderer, Matrix4x4 toRoot, Transform boundsFrom, Transform boundsBone,
                Func<int, bool> keep, Func<int, float> pull, Material material = null, string shape = null)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    throw new InvalidOperationException("Body part '" + renderer.name + "' has no readable mesh.");
                }

                // A rigid part such as an eye has a pivot of its own.
                var fromRoot = toRoot.inverse;
                var normalMatrix = fromRoot.transpose;
                var first = positions.Count;
                mesh.GetVertices(meshVertices);
                mesh.GetNormals(meshNormals);
                mesh.GetTangents(meshTangents);
                mesh.GetUVs(0, meshUvs);
                mesh.GetUVs(1, meshPaintUvs);
                if (shape != null)
                {
                    TakeShape(mesh, shape);
                }
                for (var index = 0; index < meshVertices.Count; index++)
                {
                    var vertex = meshVertices[index];
                    var normal = index < meshNormals.Count ? meshNormals[index] : Vector3.up;
                    var distance = pull != null ? pull(index) : 0f;
                    if (distance > 0f)
                    {
                        vertex -= normal.normalized * distance;
                    }

                    positions.Add(toRoot.MultiplyPoint3x4(vertex));
                    normals.Add(index < meshNormals.Count ? normalMatrix.MultiplyVector(normal).normalized : Vector3.up);
                    if (index < meshTangents.Count)
                    {
                        var tangent = toRoot.MultiplyVector(meshTangents[index]).normalized;
                        tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, meshTangents[index].w));
                    }
                    else
                    {
                        tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    }

                    uvs.Add(index < meshUvs.Count ? meshUvs[index] : Vector2.zero);
                    paintUvs.Add(index < meshPaintUvs.Count ? meshPaintUvs[index] : Vector2.zero);
                }

                // A bone keeps one bind pose for every mesh: its bind pose in the root's space.
                mesh.GetBindposes(meshBindposes);
                var meshBones = renderer.bones;
                var remap = new int[meshBones.Length];
                for (var index = 0; index < meshBones.Length; index++)
                {
                    var bone = meshBones[index].name;
                    if (!boneIndex.TryGetValue(bone, out var unified))
                    {
                        unified = bones.Count;
                        boneIndex.Add(bone, unified);
                        bones.Add(bone);
                        bindposes.Add(meshBindposes[index] * fromRoot);
                    }

                    remap[index] = unified;
                }

                var meshBonesPerVertex = mesh.GetBonesPerVertex();
                var meshWeights = mesh.GetAllBoneWeights();
                var next = weights.Count;
                for (var index = 0; index < meshBonesPerVertex.Length; index++)
                {
                    bonesPerVertex.Add(meshBonesPerVertex[index]);
                    firstWeight.Add(next);
                    next += meshBonesPerVertex[index];
                }

                for (var index = 0; index < meshWeights.Length; index++)
                {
                    var weight = meshWeights[index];
                    weight.boneIndex = remap[weight.boneIndex];
                    weights.Add(weight);
                }

                var meshMaterials = renderer.sharedMaterials;
                var triangle = 0;
                for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    var drawn = material != null ? material : submesh < meshMaterials.Length ? meshMaterials[submesh] : null;
                    var slot = materials.IndexOf(drawn);
                    if (slot < 0)
                    {
                        slot = materials.Count;
                        materials.Add(drawn);
                        triangles.Add(new List<int>());
                    }

                    mesh.GetTriangles(meshTriangles, submesh);
                    for (var corner = 0; corner < meshTriangles.Count; corner += 3, triangle++)
                    {
                        if (keep != null && !keep(triangle))
                        {
                            continue;
                        }

                        triangles[slot].Add(first + meshTriangles[corner]);
                        triangles[slot].Add(first + meshTriangles[corner + 1]);
                        triangles[slot].Add(first + meshTriangles[corner + 2]);
                    }
                }

                var meshBounds = BoundsIn(boundsFrom, renderer.localBounds, boundsBone);
                if (hasBounds)
                {
                    bounds.Encapsulate(meshBounds);
                }
                else
                {
                    bounds = meshBounds;
                    hasBounds = true;
                }
            }

            // Where a bone of the meshes added so far stands in the root's space in the bind pose.
            public bool TryGetBoneToRoot(string bone, out Matrix4x4 boneToRoot)
            {
                if (!boneIndex.TryGetValue(bone, out var index))
                {
                    boneToRoot = Matrix4x4.identity;
                    return false;
                }

                boneToRoot = bindposes[index].inverse;
                return true;
            }

            // Every mesh of a rigid model, such as a sword, given in the space of the model's root,
            // weighted fully to one bone that stands at boneToRoot in the root's space. The body's
            // bounds leave room around it for the bone to swing with the hand.
            public void AddRigid(GameObject model, string bone, Matrix4x4 boneToRoot)
            {
                if (!boneIndex.TryGetValue(bone, out var unified))
                {
                    unified = bones.Count;
                    boneIndex.Add(bone, unified);
                    bones.Add(bone);
                    bindposes.Add(boneToRoot.inverse);
                }

                var reach = 0f;
                foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = filter.sharedMesh;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (mesh == null || renderer == null)
                    {
                        continue;
                    }

                    if (!mesh.isReadable)
                    {
                        throw new InvalidOperationException("Attachment mesh '" + mesh.name + "' has no readable mesh.");
                    }

                    var toBone = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    var toRoot = boneToRoot * toBone;
                    var normalMatrix = toRoot.inverse.transpose;
                    var first = positions.Count;
                    mesh.GetVertices(meshVertices);
                    mesh.GetNormals(meshNormals);
                    mesh.GetTangents(meshTangents);
                    mesh.GetUVs(0, meshUvs);
                    for (var index = 0; index < meshVertices.Count; index++)
                    {
                        reach = Mathf.Max(reach, toBone.MultiplyPoint3x4(meshVertices[index]).magnitude);
                        positions.Add(toRoot.MultiplyPoint3x4(meshVertices[index]));
                        normals.Add(index < meshNormals.Count ? normalMatrix.MultiplyVector(meshNormals[index]).normalized : Vector3.up);
                        if (index < meshTangents.Count)
                        {
                            var tangent = toRoot.MultiplyVector(meshTangents[index]).normalized;
                            tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, meshTangents[index].w));
                        }
                        else
                        {
                            tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                        }

                        uvs.Add(index < meshUvs.Count ? meshUvs[index] : Vector2.zero);
                        paintUvs.Add(Vector2.zero);
                        bonesPerVertex.Add(1);
                        firstWeight.Add(weights.Count);
                        weights.Add(new BoneWeight1 { boneIndex = unified, weight = 1f });
                    }

                    var meshMaterials = renderer.sharedMaterials;
                    for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        var drawn = submesh < meshMaterials.Length ? meshMaterials[submesh] : null;
                        var slot = materials.IndexOf(drawn);
                        if (slot < 0)
                        {
                            slot = materials.Count;
                            materials.Add(drawn);
                            triangles.Add(new List<int>());
                        }

                        mesh.GetTriangles(meshTriangles, submesh);
                        foreach (var vertex in meshTriangles)
                        {
                            triangles[slot].Add(first + vertex);
                        }
                    }
                }

                // The bounds are in the space of the root bone in the bind pose.
                var centre = TryGetBoneToRoot(rootBone.name, out var rootBoneToRoot)
                    ? rootBoneToRoot.inverse.MultiplyPoint3x4(boneToRoot.GetColumn(3))
                    : (Vector3)boneToRoot.GetColumn(3);
                var room = new Bounds(centre, Vector3.one * (2f * (reach + AttachmentReach)));
                if (hasBounds)
                {
                    bounds.Encapsulate(room);
                }
                else
                {
                    bounds = room;
                    hasBounds = true;
                }
            }

            // Moves the mesh's vertices, normals, and tangents just read by the deltas of the last
            // frame of one of its blend shapes, its full weight.
            private void TakeShape(Mesh mesh, string shape)
            {
                var index = mesh.GetBlendShapeIndex(shape);
                if (index < 0)
                {
                    throw new InvalidOperationException("Mesh '" + mesh.name + "' has no blend shape '" + shape + "'.");
                }

                var count = mesh.vertexCount;
                var deltaVertices = new Vector3[count];
                var deltaNormals = new Vector3[count];
                var deltaTangents = new Vector3[count];
                mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, deltaVertices, deltaNormals, deltaTangents);
                for (var vertex = 0; vertex < count; vertex++)
                {
                    meshVertices[vertex] += deltaVertices[vertex];
                    if (vertex < meshNormals.Count)
                    {
                        meshNormals[vertex] = (meshNormals[vertex] + deltaNormals[vertex]).normalized;
                    }

                    if (vertex < meshTangents.Count)
                    {
                        var tangent = meshTangents[vertex];
                        var turned = (new Vector3(tangent.x, tangent.y, tangent.z) + deltaTangents[vertex]).normalized;
                        meshTangents[vertex] = new Vector4(turned.x, turned.y, turned.z, tangent.w);
                    }
                }
            }

            // The mesh of the vertices the triangles use, with the materials that kept triangles.
            public Combined Finish(string name)
            {
                var used = new int[positions.Count];
                for (var index = 0; index < used.Length; index++)
                {
                    used[index] = -1;
                }

                var keptMaterials = new List<Material>();
                var keptTriangles = new List<List<int>>();
                for (var slot = 0; slot < triangles.Count; slot++)
                {
                    if (triangles[slot].Count == 0)
                    {
                        continue;
                    }

                    keptMaterials.Add(materials[slot]);
                    keptTriangles.Add(triangles[slot]);
                    foreach (var vertex in triangles[slot])
                    {
                        used[vertex] = 0;
                    }
                }

                var count = 0;
                for (var index = 0; index < used.Length; index++)
                {
                    if (used[index] == 0)
                    {
                        used[index] = count++;
                    }
                }

                var keptPositions = new List<Vector3>(count);
                var keptNormals = new List<Vector3>(count);
                var keptTangents = new List<Vector4>(count);
                var keptUvs = new List<Vector2>(count);
                var keptPaintUvs = new List<Vector2>(count);
                var keptBonesPerVertex = new List<byte>(count);
                var keptWeights = new List<BoneWeight1>();
                for (var index = 0; index < used.Length; index++)
                {
                    if (used[index] < 0)
                    {
                        continue;
                    }

                    keptPositions.Add(positions[index]);
                    keptNormals.Add(normals[index]);
                    keptTangents.Add(tangents[index]);
                    keptUvs.Add(uvs[index]);
                    keptPaintUvs.Add(paintUvs[index]);
                    keptBonesPerVertex.Add(bonesPerVertex[index]);
                    for (var weight = 0; weight < bonesPerVertex[index]; weight++)
                    {
                        keptWeights.Add(weights[firstWeight[index] + weight]);
                    }
                }

                var combinedMesh = new Mesh
                {
                    name = name,
                    indexFormat = count > ushort.MaxValue
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                combinedMesh.SetVertices(keptPositions);
                combinedMesh.SetNormals(keptNormals);
                combinedMesh.SetTangents(keptTangents);
                combinedMesh.SetUVs(0, keptUvs);
                combinedMesh.SetUVs(1, keptPaintUvs);
                using (var perVertex = new NativeArray<byte>(keptBonesPerVertex.ToArray(), Allocator.Temp))
                using (var allWeights = new NativeArray<BoneWeight1>(keptWeights.ToArray(), Allocator.Temp))
                {
                    combinedMesh.SetBoneWeights(perVertex, allWeights);
                }

                combinedMesh.bindposes = bindposes.ToArray();
                combinedMesh.subMeshCount = keptTriangles.Count;
                for (var submesh = 0; submesh < keptTriangles.Count; submesh++)
                {
                    var remapped = new List<int>(keptTriangles[submesh].Count);
                    foreach (var vertex in keptTriangles[submesh])
                    {
                        remapped.Add(used[vertex]);
                    }

                    combinedMesh.SetTriangles(remapped, submesh, false);
                }

                combinedMesh.RecalculateBounds();
                return new Combined(combinedMesh, bones.ToArray(), keptMaterials.ToArray(), rootBone.name, bounds);
            }
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
