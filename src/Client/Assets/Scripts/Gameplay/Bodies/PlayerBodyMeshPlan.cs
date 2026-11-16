using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // How PlayerBodyMeshes builds a combination: the data of the source meshes, the meshes of one
    // combination as plain data, the buffers a build reuses, and the plan that gathers, computes,
    // and applies it; see the header of PlayerBodyMeshes.
    public sealed partial class PlayerBodyMeshes
    {
        // What a build reads of a source mesh, a body part's, a garment's, or an attachment's: read
        // once per set on the main thread and then shared, unchanged, by every build on any thread.
        private sealed class SourceMesh
        {
            private readonly Dictionary<string, Vector3[][]> shapes = new Dictionary<string, Vector3[][]>(StringComparer.Ordinal);

            public SourceMesh(Mesh mesh, bool paintUvs)
            {
                Vertices = mesh.vertices;
                Normals = mesh.normals;
                Tangents = mesh.tangents;
                var uvs = new List<Vector2>();
                mesh.GetUVs(0, uvs);
                Uvs = uvs.ToArray();
                if (paintUvs)
                {
                    mesh.GetUVs(1, uvs);
                    PaintUvs = uvs.ToArray();
                }
                else
                {
                    PaintUvs = Array.Empty<Vector2>();
                }

                Bindposes = mesh.bindposes;
                BonesPerVertex = mesh.GetBonesPerVertex().ToArray();
                Weights = mesh.GetAllBoneWeights().ToArray();
                Triangles = new int[mesh.subMeshCount][];
                for (var submesh = 0; submesh < Triangles.Length; submesh++)
                {
                    Triangles[submesh] = mesh.GetTriangles(submesh);
                }
            }

            public Vector3[] Vertices { get; }

            public Vector3[] Normals { get; }

            public Vector4[] Tangents { get; }

            public Vector2[] Uvs { get; }

            // The paint UVs, the body's second UV map; empty on meshes without it.
            public Vector2[] PaintUvs { get; }

            public Matrix4x4[] Bindposes { get; }

            public byte[] BonesPerVertex { get; }

            public BoneWeight1[] Weights { get; }

            public int[][] Triangles { get; }

            // The deltas of the vertices, normals, and tangents of the last frame of a blend shape,
            // its full weight.
            public Vector3[][] Shape(Mesh mesh, string name)
            {
                if (!shapes.TryGetValue(name, out var deltas))
                {
                    var index = mesh.GetBlendShapeIndex(name);
                    if (index < 0)
                    {
                        throw new InvalidOperationException("Mesh '" + mesh.name + "' has no blend shape '" + name + "'.");
                    }

                    var count = mesh.vertexCount;
                    deltas = new[] { new Vector3[count], new Vector3[count], new Vector3[count] };
                    mesh.GetBlendShapeFrameVertices(index, mesh.GetBlendShapeFrameCount(index) - 1, deltas[0], deltas[1], deltas[2]);
                    shapes[name] = deltas;
                }

                return deltas;
            }

            // The source of a mesh, read on first use.
            public static SourceMesh Of(Mesh mesh, bool paintUvs, Dictionary<Mesh, SourceMesh> sources)
            {
                if (!sources.TryGetValue(mesh, out var source))
                {
                    source = new SourceMesh(mesh, paintUvs);
                    sources[mesh] = source;
                }

                return source;
            }
        }

        // One mesh of a combination: its source, where it lies, which bones and material slots its
        // own map to, which of its triangles stay, how far its vertices move inward, and what it
        // adds to the bounds.
        private sealed class Piece
        {
            public SourceMesh Source;
            public int[] BoneRemap;
            public int[] Slots;
            public Matrix4x4 ToRoot;
            public Matrix4x4 NormalMatrix;

            // A blend shape taken in full, such as hair's form under headwear: its deltas.
            public Vector3[][] Shape;

            // A body part's map, whose faces a drawn garment covers and whose vertices it pulls in.
            public BodyAppearanceCatalog.Part Map;

            // A garment, whose triangles a garment of a higher layer covers.
            public bool IsGarment;
            public IReadOnlyList<int> Underlay;
            public int Layer;

            // A rigid attachment's mesh, weighted fully to one bone.
            public bool IsRigid;
            public int RigidBone;

            // The bounds after the mesh, or room around a held model: its vertices count toward the
            // reach of the model through ReachMatrix, and the last mesh of the model makes the room.
            public bool HasBounds;
            public Bounds Bounds;
            public bool Reaches;
            public Matrix4x4 ReachMatrix;
            public bool EndsRoom;
            public Vector3 RoomCentre;
        }

        // The lists one build joins its meshes in and keeps its mesh in, used again by the next
        // build, so a build makes little garbage.
        private sealed class Buffers
        {
            public readonly Dictionary<int, int> CoverLayer = new Dictionary<int, int>();
            public readonly Dictionary<int, float> PullIn = new Dictionary<int, float>();
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Vector2> PaintUvs = new List<Vector2>();
            public readonly List<byte> BonesPerVertex = new List<byte>();
            public readonly List<int> FirstWeight = new List<int>();
            public readonly List<BoneWeight1> Weights = new List<BoneWeight1>();
            public readonly List<List<int>> Triangles = new List<List<int>>();
            public readonly List<int> Used = new List<int>();
            public readonly List<Vector3> KeptPositions = new List<Vector3>();
            public readonly List<Vector3> KeptNormals = new List<Vector3>();
            public readonly List<Vector4> KeptTangents = new List<Vector4>();
            public readonly List<Vector2> KeptUvs = new List<Vector2>();
            public readonly List<Vector2> KeptPaintUvs = new List<Vector2>();
            public readonly List<byte> KeptBonesPerVertex = new List<byte>();
            public readonly List<BoneWeight1> KeptWeights = new List<BoneWeight1>();
            // The slots that kept triangles, in order; their triangles are remapped in place.
            public readonly List<int> KeptSlots = new List<int>();

            public void Clear(int slots)
            {
                CoverLayer.Clear();
                PullIn.Clear();
                Positions.Clear();
                Normals.Clear();
                Tangents.Clear();
                Uvs.Clear();
                PaintUvs.Clear();
                BonesPerVertex.Clear();
                FirstWeight.Clear();
                Weights.Clear();
                while (Triangles.Count < slots)
                {
                    Triangles.Add(new List<int>());
                }

                foreach (var list in Triangles)
                {
                    list.Clear();
                }

                Used.Clear();
                KeptPositions.Clear();
                KeptNormals.Clear();
                KeptTangents.Clear();
                KeptUvs.Clear();
                KeptPaintUvs.Clear();
                KeptBonesPerVertex.Clear();
                KeptWeights.Clear();
                KeptSlots.Clear();
            }
        }

        // A combination as plain data. Gather reads every renderer, model, and transform on the main
        // thread; Compute joins the vertices and triangles, touches no Unity object, and may run on
        // any thread; Apply makes the mesh on the main thread. Gather, Compute, and Apply in a row
        // build exactly what Compute on another thread does.
        private sealed class Plan
        {
            private readonly List<string> bones = new List<string>();
            private readonly Dictionary<string, int> boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<Matrix4x4> bindposes = new List<Matrix4x4>();
            private readonly List<Material> materials = new List<Material>();
            private readonly List<Piece> pieces = new List<Piece>();
            private readonly Dictionary<Mesh, SourceMesh> sources;
            private List<BodyAppearanceCatalog.Garment> drawn;
            private string name;
            private string rootBoneName;
            private Buffers buffers;
            private Bounds bounds;
            private bool hasBounds;

            private Plan(Dictionary<Mesh, SourceMesh> sources)
            {
                this.sources = sources;
            }

            public static Plan Gather(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
                IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments,
                Dictionary<Mesh, SourceMesh> sources)
            {
                if (parts == null || parts.Count == 0)
                {
                    throw new ArgumentException("A combined body needs at least one part.", nameof(parts));
                }

                var plan = new Plan(sources);
                plan.drawn = Drawn(garments);
                var rootBone = parts[0].rootBone != null ? parts[0].rootBone : root;
                plan.rootBoneName = rootBone.name;
                plan.name = parts[0].sharedMesh.name + " combined";
                foreach (var part in parts)
                {
                    var piece = plan.Read(part, root.worldToLocalMatrix * part.transform.localToWorldMatrix, null, null);
                    piece.Map = catalog != null ? catalog.PartNamed(part.name) : null;
                    piece.HasBounds = true;
                    piece.Bounds = BoundsIn(part.rootBone != null ? part.rootBone : part.transform, part.localBounds, rootBone);
                }

                foreach (var garment in plan.drawn)
                {
                    var renderer = garment.Model.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    if (renderer == null || renderer.sharedMesh == null)
                    {
                        continue;
                    }

                    // The bones of the garment's own armature stand where the body's do, so its bounds
                    // carry over from its root bone to the body's root bone of the same name.
                    var garmentRootBone = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
                    var bodyRootBone = FindDescendant(garment.Model.transform, plan.rootBoneName) ?? garmentRootBone;
                    var piece = plan.Read(renderer, garment.Model.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                        garment.Material, ShapeOf(garment, plan.drawn));
                    piece.IsGarment = true;
                    piece.Underlay = garment.Underlay;
                    piece.Layer = garment.Layer;
                    piece.HasBounds = true;
                    piece.Bounds = BoundsIn(garmentRootBone, renderer.localBounds, bodyRootBone);
                }

                foreach (var attachment in Held(attachments))
                {
                    var socket = FindDescendant(root, attachment.Bone);
                    if (socket == null || socket.parent == null || !plan.TryGetBoneToRoot(socket.parent.name, out var parentToRoot))
                    {
                        continue;
                    }

                    // A socket is never animated, so it stands on its parent as it does in the model.
                    var socketToRoot = parentToRoot * Matrix4x4.TRS(socket.localPosition, socket.localRotation, socket.localScale);
                    if (attachment.IsSkinned)
                    {
                        plan.ReadSkinned(attachment.Model, socketToRoot);
                    }
                    else
                    {
                        plan.ReadRigid(attachment.Model, attachment.Bone, socketToRoot);
                    }
                }

                return plan;
            }

            // One renderer's mesh, given in the root's space by toRoot: its source, its bones in the
            // plan's list, and its submeshes in the plan's material slots. A material, such as a
            // garment texture's, replaces the renderer's own; a blend shape of the mesh is taken in
            // full.
            private Piece Read(SkinnedMeshRenderer renderer, Matrix4x4 toRoot, Material material, string shape)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || !mesh.isReadable)
                {
                    throw new InvalidOperationException("Body part '" + renderer.name + "' has no readable mesh.");
                }

                // A rigid part such as an eye has a pivot of its own.
                var fromRoot = toRoot.inverse;
                var source = SourceMesh.Of(mesh, true, sources);
                var piece = new Piece
                {
                    Source = source,
                    ToRoot = toRoot,
                    NormalMatrix = fromRoot.transpose,
                    Shape = shape != null ? source.Shape(mesh, shape) : null,
                };

                // A bone keeps one bind pose for every mesh: its bind pose in the root's space.
                var meshBones = renderer.bones;
                piece.BoneRemap = new int[meshBones.Length];
                for (var index = 0; index < meshBones.Length; index++)
                {
                    var bone = meshBones[index].name;
                    if (!boneIndex.TryGetValue(bone, out var unified))
                    {
                        unified = bones.Count;
                        boneIndex.Add(bone, unified);
                        bones.Add(bone);
                        bindposes.Add(source.Bindposes[index] * fromRoot);
                    }

                    piece.BoneRemap[index] = unified;
                }

                piece.Slots = SlotsOf(source, renderer.sharedMaterials, material);
                pieces.Add(piece);
                return piece;
            }

            // The material slot of each submesh, in the order the materials first appear.
            private int[] SlotsOf(SourceMesh source, Material[] meshMaterials, Material material)
            {
                var slots = new int[source.Triangles.Length];
                for (var submesh = 0; submesh < slots.Length; submesh++)
                {
                    var drawnMaterial = material != null ? material : submesh < meshMaterials.Length ? meshMaterials[submesh] : null;
                    var slot = materials.IndexOf(drawnMaterial);
                    if (slot < 0)
                    {
                        slot = materials.Count;
                        materials.Add(drawnMaterial);
                    }

                    slots[submesh] = slot;
                }

                return slots;
            }

            // Every mesh of a skinned model held on a socket, such as a bow, given in the space of the
            // model's root, which lies on the socket that stands at socketToRoot in the root's space,
            // skinned to the model's own bones, which a body holds below the socket as they stand below
            // the model's root. A model with a bone of the meshes read so far is left out, so a body
            // holds one model of a skeleton. The body's bounds leave room around it for the socket to
            // swing with the hand.
            private void ReadSkinned(GameObject model, Matrix4x4 socketToRoot)
            {
                var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var renderer in renderers)
                {
                    foreach (var bone in renderer.bones)
                    {
                        if (bone == null || boneIndex.ContainsKey(bone.name))
                        {
                            return;
                        }
                    }
                }

                Piece last = null;
                foreach (var renderer in renderers)
                {
                    if (renderer.sharedMesh == null)
                    {
                        continue;
                    }

                    var toSocket = model.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                    last = Read(renderer, socketToRoot * toSocket, null, null);
                    last.Reaches = true;
                    last.ReachMatrix = toSocket;
                }

                EndRoom(last, socketToRoot);
            }

            // Every mesh of a rigid model, such as a sword, given in the space of the model's root,
            // weighted fully to one bone that stands at boneToRoot in the root's space. The body's
            // bounds leave room around it for the bone to swing with the hand.
            private void ReadRigid(GameObject model, string bone, Matrix4x4 boneToRoot)
            {
                if (!boneIndex.TryGetValue(bone, out var unified))
                {
                    unified = bones.Count;
                    boneIndex.Add(bone, unified);
                    bones.Add(bone);
                    bindposes.Add(boneToRoot.inverse);
                }

                Piece last = null;
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
                    var source = SourceMesh.Of(mesh, false, sources);
                    last = new Piece
                    {
                        Source = source,
                        ToRoot = toRoot,
                        NormalMatrix = toRoot.inverse.transpose,
                        IsRigid = true,
                        RigidBone = unified,
                        Reaches = true,
                        ReachMatrix = toBone,
                        Slots = SlotsOf(source, renderer.sharedMaterials, null),
                    };
                    pieces.Add(last);
                }

                EndRoom(last, boneToRoot);
            }

            // The last mesh of a held model makes room in the bounds for what the bone at boneToRoot
            // holds within reach of it, in the space of the root bone in the bind pose.
            private void EndRoom(Piece last, Matrix4x4 boneToRoot)
            {
                if (last == null)
                {
                    // A model without meshes still makes room around its bone.
                    last = new Piece { Slots = Array.Empty<int>() };
                    pieces.Add(last);
                }

                last.EndsRoom = true;
                last.RoomCentre = TryGetBoneToRoot(rootBoneName, out var rootBoneToRoot)
                    ? rootBoneToRoot.inverse.MultiplyPoint3x4(boneToRoot.GetColumn(3))
                    : (Vector3)boneToRoot.GetColumn(3);
            }

            // Where a bone of the meshes read so far stands in the root's space in the bind pose.
            private bool TryGetBoneToRoot(string bone, out Matrix4x4 boneToRoot)
            {
                if (!boneIndex.TryGetValue(bone, out var index))
                {
                    boneToRoot = Matrix4x4.identity;
                    return false;
                }

                boneToRoot = bindposes[index].inverse;
                return true;
            }

            // Joins the vertices, weights, and triangles of every mesh in the root's space in the
            // buffers and leaves out the vertices no triangle uses. Touches no Unity object, so it
            // runs on any thread.
            public void Compute(Buffers into)
            {
                buffers = into;
                into.Clear(materials.Count);
                var coverLayer = into.CoverLayer;
                var pullIn = into.PullIn;
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

                var reach = 0f;
                foreach (var piece in pieces)
                {
                    var first = into.Positions.Count;
                    if (piece.Source != null && piece.IsRigid)
                    {
                        AddRigid(piece, into, ref reach);
                    }
                    else if (piece.Source != null)
                    {
                        AddSkinned(piece, into);
                        if (piece.Reaches)
                        {
                            foreach (var vertex in piece.Source.Vertices)
                            {
                                reach = Mathf.Max(reach, piece.ReachMatrix.MultiplyPoint3x4(vertex).magnitude);
                            }
                        }
                    }

                    if (piece.Source != null)
                    {
                        AddTriangles(piece, first, coverLayer, into.Triangles);
                    }

                    if (piece.HasBounds)
                    {
                        Encapsulate(piece.Bounds);
                    }

                    if (piece.EndsRoom)
                    {
                        Encapsulate(new Bounds(piece.RoomCentre, Vector3.one * (2f * (reach + AttachmentReach))));
                        reach = 0f;
                    }
                }

                Finish(into);
            }

            private static void AddSkinned(Piece piece, Buffers into)
            {
                var source = piece.Source;
                var vertices = source.Vertices;
                var meshNormals = source.Normals;
                var meshTangents = source.Tangents;
                var meshUvs = source.Uvs;
                var meshPaintUvs = source.PaintUvs;
                var shape = piece.Shape;
                var map = piece.Map;
                var pulls = map != null && into.PullIn.Count > 0;
                for (var index = 0; index < vertices.Length; index++)
                {
                    var vertex = vertices[index];
                    var normal = index < meshNormals.Length ? meshNormals[index] : Vector3.up;
                    var meshTangent = index < meshTangents.Length ? meshTangents[index] : default(Vector4);
                    if (shape != null)
                    {
                        // The blend shape's full weight moves the vertex, and turns its normal and
                        // its tangent, before anything else.
                        vertex += shape[0][index];
                        if (index < meshNormals.Length)
                        {
                            normal = (normal + shape[1][index]).normalized;
                        }

                        if (index < meshTangents.Length)
                        {
                            var turned = (new Vector3(meshTangent.x, meshTangent.y, meshTangent.z) + shape[2][index]).normalized;
                            meshTangent = new Vector4(turned.x, turned.y, turned.z, meshTangent.w);
                        }
                    }

                    var distance = pulls && into.PullIn.TryGetValue(map.VertexSources[index], out var pulled) ? pulled : 0f;
                    if (distance > 0f)
                    {
                        vertex -= normal.normalized * distance;
                    }

                    into.Positions.Add(piece.ToRoot.MultiplyPoint3x4(vertex));
                    into.Normals.Add(index < meshNormals.Length ? piece.NormalMatrix.MultiplyVector(normal).normalized : Vector3.up);
                    if (index < meshTangents.Length)
                    {
                        var tangent = piece.ToRoot.MultiplyVector(meshTangent).normalized;
                        into.Tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, meshTangent.w));
                    }
                    else
                    {
                        into.Tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    }

                    into.Uvs.Add(index < meshUvs.Length ? meshUvs[index] : Vector2.zero);
                    into.PaintUvs.Add(index < meshPaintUvs.Length ? meshPaintUvs[index] : Vector2.zero);
                }

                var next = into.Weights.Count;
                for (var index = 0; index < source.BonesPerVertex.Length; index++)
                {
                    into.BonesPerVertex.Add(source.BonesPerVertex[index]);
                    into.FirstWeight.Add(next);
                    next += source.BonesPerVertex[index];
                }

                for (var index = 0; index < source.Weights.Length; index++)
                {
                    var weight = source.Weights[index];
                    weight.boneIndex = piece.BoneRemap[weight.boneIndex];
                    into.Weights.Add(weight);
                }
            }

            private static void AddRigid(Piece piece, Buffers into, ref float reach)
            {
                var source = piece.Source;
                var vertices = source.Vertices;
                var meshNormals = source.Normals;
                var meshTangents = source.Tangents;
                var meshUvs = source.Uvs;
                for (var index = 0; index < vertices.Length; index++)
                {
                    reach = Mathf.Max(reach, piece.ReachMatrix.MultiplyPoint3x4(vertices[index]).magnitude);
                    into.Positions.Add(piece.ToRoot.MultiplyPoint3x4(vertices[index]));
                    into.Normals.Add(index < meshNormals.Length ? piece.NormalMatrix.MultiplyVector(meshNormals[index]).normalized : Vector3.up);
                    if (index < meshTangents.Length)
                    {
                        var tangent = piece.ToRoot.MultiplyVector(meshTangents[index]).normalized;
                        into.Tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, meshTangents[index].w));
                    }
                    else
                    {
                        into.Tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                    }

                    into.Uvs.Add(index < meshUvs.Length ? meshUvs[index] : Vector2.zero);
                    into.PaintUvs.Add(Vector2.zero);
                    into.BonesPerVertex.Add(1);
                    into.FirstWeight.Add(into.Weights.Count);
                    into.Weights.Add(new BoneWeight1 { boneIndex = piece.RigidBone, weight = 1f });
                }
            }

            // The triangles of one mesh that stay, over its submeshes in order, in their material
            // slots: a body face stays unless a drawn garment covers it, and a garment face unless a
            // garment of a higher layer covers the body face under it.
            private static void AddTriangles(Piece piece, int first, Dictionary<int, int> coverLayer, List<List<int>> triangles)
            {
                var map = piece.Map;
                var coversBody = map != null && map.TriangleFaces.Count > 0;
                var triangle = 0;
                var sourceTriangles = piece.Source.Triangles;
                for (var submesh = 0; submesh < sourceTriangles.Length; submesh++)
                {
                    var slot = triangles[piece.Slots[submesh]];
                    var meshTriangles = sourceTriangles[submesh];
                    for (var corner = 0; corner < meshTriangles.Length; corner += 3, triangle++)
                    {
                        if (coversBody && coverLayer.ContainsKey(map.TriangleFaces[triangle]))
                        {
                            continue;
                        }

                        if (piece.IsGarment && triangle < piece.Underlay.Count
                            && coverLayer.TryGetValue(piece.Underlay[triangle], out var layer) && layer > piece.Layer)
                        {
                            continue;
                        }

                        slot.Add(first + meshTriangles[corner]);
                        slot.Add(first + meshTriangles[corner + 1]);
                        slot.Add(first + meshTriangles[corner + 2]);
                    }
                }
            }

            private void Encapsulate(Bounds more)
            {
                if (hasBounds)
                {
                    bounds.Encapsulate(more);
                }
                else
                {
                    bounds = more;
                    hasBounds = true;
                }
            }

            // The vertices the triangles use, with the slots that kept triangles, whose triangles
            // then name the kept vertices.
            private void Finish(Buffers into)
            {
                var used = into.Used;
                for (var index = 0; index < into.Positions.Count; index++)
                {
                    used.Add(-1);
                }

                for (var slot = 0; slot < materials.Count; slot++)
                {
                    var triangles = into.Triangles[slot];
                    if (triangles.Count == 0)
                    {
                        continue;
                    }

                    into.KeptSlots.Add(slot);
                    foreach (var vertex in triangles)
                    {
                        used[vertex] = 0;
                    }
                }

                var count = 0;
                for (var index = 0; index < used.Count; index++)
                {
                    if (used[index] < 0)
                    {
                        continue;
                    }

                    used[index] = count++;
                    into.KeptPositions.Add(into.Positions[index]);
                    into.KeptNormals.Add(into.Normals[index]);
                    into.KeptTangents.Add(into.Tangents[index]);
                    into.KeptUvs.Add(into.Uvs[index]);
                    into.KeptPaintUvs.Add(into.PaintUvs[index]);
                    into.KeptBonesPerVertex.Add(into.BonesPerVertex[index]);
                    for (var weight = 0; weight < into.BonesPerVertex[index]; weight++)
                    {
                        into.KeptWeights.Add(into.Weights[into.FirstWeight[index] + weight]);
                    }
                }

                foreach (var slot in into.KeptSlots)
                {
                    var triangles = into.Triangles[slot];
                    for (var index = 0; index < triangles.Count; index++)
                    {
                        triangles[index] = used[triangles[index]];
                    }
                }
            }

            // The mesh of what Compute made, after which its buffers serve another build; with
            // releaseCpuCopy it keeps no copy once uploaded.
            public Combined Apply(bool releaseCpuCopy)
            {
                var into = buffers;
                buffers = null;
                var count = into.KeptPositions.Count;
                var combinedMesh = new Mesh
                {
                    name = name,
                    indexFormat = count > ushort.MaxValue
                        ? UnityEngine.Rendering.IndexFormat.UInt32
                        : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                combinedMesh.SetVertices(into.KeptPositions);
                combinedMesh.SetNormals(into.KeptNormals);
                combinedMesh.SetTangents(into.KeptTangents);
                combinedMesh.SetUVs(0, into.KeptUvs);
                combinedMesh.SetUVs(1, into.KeptPaintUvs);
                var perVertex = new NativeArray<byte>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var allWeights = new NativeArray<BoneWeight1>(into.KeptWeights.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        perVertex[index] = into.KeptBonesPerVertex[index];
                    }

                    for (var index = 0; index < allWeights.Length; index++)
                    {
                        allWeights[index] = into.KeptWeights[index];
                    }

                    combinedMesh.SetBoneWeights(perVertex, allWeights);
                }
                finally
                {
                    perVertex.Dispose();
                    allWeights.Dispose();
                }

                combinedMesh.bindposes = bindposes.ToArray();
                combinedMesh.subMeshCount = into.KeptSlots.Count;
                var keptMaterials = new Material[into.KeptSlots.Count];
                for (var submesh = 0; submesh < into.KeptSlots.Count; submesh++)
                {
                    var slot = into.KeptSlots[submesh];
                    keptMaterials[submesh] = materials[slot];
                    combinedMesh.SetTriangles(into.Triangles[slot], submesh, false);
                }

                combinedMesh.RecalculateBounds();
                if (releaseCpuCopy)
                {
                    combinedMesh.UploadMeshData(true);
                }

                return new Combined(combinedMesh, bones.ToArray(), keptMaterials, rootBoneName, bounds);
            }

            // The buffers of the build, which Apply gives back.
            public Buffers Buffers
            {
                get { return buffers; }
            }
        }
    }
}
