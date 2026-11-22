using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Writes the appearance catalog of every exported skinned model that names variants in its
    // asset.json or that a paint layer or a garment fits: one slot per painted material with
    // variants, its own image first, the paint layers of the body in the order they are drawn, the
    // body parts with the faces of the editable mesh their triangles come from, and the garments of
    // the body from the lowest layer up, one per garment texture, each its template's data, and the
    // attachments its sockets can hold, as Assets/Art/Appearance/<Model>_Appearance.asset. The
    // catalog names its content by Addressables address: this writes a BodyPieceContent per garment
    // and body, Assets/Content/Bodies/Garments/<Model>/<Garment>.asset with the template's model for
    // the body and the texture's material, and one per attachment,
    // Assets/Content/Bodies/Attachments/<Attachment>.asset with its model and clips, and makes them
    // and the images of the options and paint layers entries of the Body Content group, each its own
    // bundle, and every asset two of them share an entry of the Body Content Shared group; see
    // ArtBodyContentEntries. It writes only what differs, so a second run changes nothing, and
    // ArtExportFollowUp runs it after every export. ArtAppearanceCatalogTests fails when a catalog,
    // a content asset, or an entry differs from what this writes. See docs/ART_PIPELINE.md.
    public static class ArtAppearanceCatalogs
    {
        public const string Folder = "Assets/Art/Appearance";
        public const string ContentFolder = ClientAddresses.ContentRoot + "Bodies";
        public const string GarmentContentFolder = ContentFolder + "/Garments";
        public const string AttachmentContentFolder = ContentFolder + "/Attachments";
        public const string PaintAddressPrefix = "Bodies/Paints/";
        public const string OptionAddressPrefix = "Bodies/Options/";
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Update Appearance Catalogs";

        [MenuItem(MenuPath)]
        public static void UpdateAllMenu()
        {
            var written = UpdateAll();
            Debug.Log(written == 0
                ? "Every appearance catalog, its content, and its Addressables entries are up to date."
                : "Wrote " + written + " appearance catalog(s), content asset(s), or Addressables change(s).");
        }

        // Writes every catalog, content asset, and Addressables entry that is missing or differs
        // from the exports, removes the content assets no catalog names, and returns how many
        // things it wrote.
        public static int UpdateAll()
        {
            var looks = ManifestsWithLooks().Select(LooksFor).ToArray();
            var written = WriteContent(looks);
            foreach (var body in looks)
            {
                var path = PathFor(body.Manifest);
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                if (catalog != null && Matches(catalog, body.Slots, body.Paints) && Matches(catalog, body.Parts, body.Garments)
                    && Matches(catalog, body.Attachments))
                {
                    continue;
                }

                if (catalog == null)
                {
                    Directory.CreateDirectory(Folder);
                    catalog = ScriptableObject.CreateInstance<BodyAppearanceCatalog>();
                    catalog.Configure(body.Slots, body.Paints, body.Parts, body.Garments, body.Attachments);
                    AssetDatabase.CreateAsset(catalog, path);
                }
                else
                {
                    catalog.Configure(body.Slots, body.Paints, body.Parts, body.Garments, body.Attachments);
                    EditorUtility.SetDirty(catalog);
                }

                written++;
                Debug.Log("Wrote the appearance catalog " + path + " with " + string.Join(", ",
                    body.Slots.Select(slot => slot.Name + " (" + string.Join(", ", slot.Options.Select(option => option.Name)) + ")")
                        .Concat(body.Paints.Select(paint => "Paint " + paint.Name))
                        .Concat(body.Garments.Select(garment => "Garment " + garment.Name))
                        .Concat(body.Attachments.Select(attachment => "Attachment " + attachment.Name))));
            }

            if (written > 0)
            {
                AssetDatabase.SaveAssets();
            }

            written += ArtBodyContentEntries.Apply(ContentEntries(looks));
            return written;
        }

        // What the catalog of a body holds and the content it names.
        public sealed class BodyLooks
        {
            public ArtAssetManifest Manifest;
            public BodyAppearanceCatalog.Slot[] Slots;
            public BodyAppearanceCatalog.Paint[] Paints;
            public BodyAppearanceCatalog.Part[] Parts;
            public BodyAppearanceCatalog.Garment[] Garments;
            public BodyAppearanceCatalog.Attachment[] Attachments;
            public PieceSpec[] Pieces;
            public TextureSpec[] Textures;
        }

        // A garment's or an attachment's content asset, at its path, with what it holds.
        public sealed class PieceSpec
        {
            public string Path;
            public string Address;
            public GameObject Model;
            public Material Material;
            public AnimationClip[] Clips;
            public BodyPieceContent.EndRotation[] EndRotations;
        }

        // The image of an option or a paint layer, an entry of its own.
        public sealed class TextureSpec
        {
            public string Address;
            public Texture2D Texture;
        }

        public static BodyLooks LooksFor(ArtAssetManifest manifest)
        {
            var slots = SlotsFor(manifest, out var options);
            var paints = PaintsFor(manifest, out var paintTextures);
            var garments = GarmentsFor(manifest, out var garmentPieces);
            var attachments = AttachmentsFor(manifest, out var attachmentPieces);
            return new BodyLooks
            {
                Manifest = manifest,
                Slots = slots,
                Paints = paints,
                Parts = PartsFor(manifest),
                Garments = garments,
                Attachments = attachments,
                Pieces = garmentPieces.Concat(attachmentPieces).ToArray(),
                Textures = options.Concat(paintTextures).ToArray(),
            };
        }

        // The address of an asset below the content root: its path below it.
        public static string ContentAddressOf(string path)
        {
            return path.Substring(ClientAddresses.ContentRoot.Length);
        }

        public static string GarmentContentPath(ArtAssetManifest body, string garment)
        {
            return GarmentContentFolder + "/" + Path.GetFileNameWithoutExtension(body.target) + "/" + garment + ".asset";
        }

        public static string AttachmentContentPath(string attachment)
        {
            return AttachmentContentFolder + "/" + attachment + ".asset";
        }

        // Every Addressables entry of the body content: the content assets and the images, by
        // address.
        public static IReadOnlyDictionary<string, string> ContentEntries(IEnumerable<BodyLooks> looks)
        {
            var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var body in looks)
            {
                foreach (var piece in body.Pieces)
                {
                    entries[piece.Address] = piece.Path;
                }

                foreach (var texture in body.Textures)
                {
                    entries[texture.Address] = AssetDatabase.GetAssetPath(texture.Texture);
                }
            }

            return entries;
        }

        // Creates or updates the content asset of every garment and attachment, removes those no
        // catalog names, and returns how many it wrote or removed.
        public static int WriteContent(IReadOnlyList<BodyLooks> looks)
        {
            var written = 0;
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var piece in looks.SelectMany(body => body.Pieces))
            {
                if (!expected.Add(piece.Path))
                {
                    continue;
                }

                var content = AssetDatabase.LoadAssetAtPath<BodyPieceContent>(piece.Path);
                if (content != null && Matches(content, piece))
                {
                    continue;
                }

                if (content == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(piece.Path));
                    content = ScriptableObject.CreateInstance<BodyPieceContent>();
                    content.Configure(piece.Model, piece.Material, piece.Clips, piece.EndRotations);
                    AssetDatabase.CreateAsset(content, piece.Path);
                }
                else
                {
                    content.Configure(piece.Model, piece.Material, piece.Clips, piece.EndRotations);
                    EditorUtility.SetDirty(content);
                }

                written++;
            }

            if (AssetDatabase.IsValidFolder(ContentFolder))
            {
                foreach (var stale in AssetDatabase.FindAssets("t:BodyPieceContent", new[] { ContentFolder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => !expected.Contains(path))
                    .ToArray())
                {
                    AssetDatabase.DeleteAsset(stale);
                    written++;
                }
            }

            if (written > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return written;
        }

        public static bool Matches(BodyPieceContent content, PieceSpec piece)
        {
            var ends = piece.EndRotations ?? Array.Empty<BodyPieceContent.EndRotation>();
            return content.Model == piece.Model
                && content.Material == piece.Material
                && content.Clips.SequenceEqual(piece.Clips ?? Array.Empty<AnimationClip>())
                && content.EndRotations.Count == ends.Length
                && content.EndRotations.Zip(ends, (actual, expected) =>
                    actual.Clip == expected.Clip
                    && actual.Bone == expected.Bone
                    && actual.Rotation.Equals(expected.Rotation))
                    .All(same => same);
        }

        public static bool Matches(BodyAppearanceCatalog catalog, BodyAppearanceCatalog.Slot[] slots, BodyAppearanceCatalog.Paint[] paints)
        {
            return catalog.Slots.Count == slots.Length
                && catalog.Slots.Zip(slots, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Material == expected.Material
                    && actual.Options.Select(option => option.Name).SequenceEqual(expected.Options.Select(option => option.Name))
                    && actual.Options.Select(option => option.Address).SequenceEqual(expected.Options.Select(option => option.Address)))
                    .All(same => same)
                && catalog.Paints.Count == paints.Length
                && catalog.Paints.Zip(paints, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Category == expected.Category
                    && actual.Slot == expected.Slot
                    && actual.Material == expected.Material
                    && actual.Address == expected.Address
                    && actual.Rect == expected.Rect
                    && actual.Layer == expected.Layer
                    && actual.Smoothness.Equals(expected.Smoothness)
                    && actual.Metallic.Equals(expected.Metallic))
                    .All(same => same);
        }

        public static bool Matches(BodyAppearanceCatalog catalog, BodyAppearanceCatalog.Part[] parts,
            BodyAppearanceCatalog.Garment[] garments)
        {
            return catalog.Parts.Count == parts.Length
                && catalog.Parts.Zip(parts, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.TriangleFaces.SequenceEqual(expected.TriangleFaces)
                    && actual.VertexSources.SequenceEqual(expected.VertexSources))
                    .All(same => same)
                && catalog.Garments.Count == garments.Length
                && catalog.Garments.Zip(garments, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Template == expected.Template
                    && actual.Address == expected.Address
                    && actual.Slot == expected.Slot
                    && actual.Layer == expected.Layer
                    && actual.Hides.SequenceEqual(expected.Hides)
                    && actual.HidesUnderwear == expected.HidesUnderwear
                    && actual.Hair == expected.Hair
                    && actual.Cloth == expected.Cloth
                    && actual.Covered.SequenceEqual(expected.Covered)
                    && actual.Underlay.SequenceEqual(expected.Underlay)
                    && actual.PullInVertices.SequenceEqual(expected.PullInVertices)
                    && actual.PullInDistances.SequenceEqual(expected.PullInDistances)
                    && actual.HiddenInner.Count == expected.HiddenInner.Count
                    && actual.HiddenInner.Zip(expected.HiddenInner, (a, b) =>
                        a.Template == b.Template && a.Triangles.SequenceEqual(b.Triangles)).All(same => same))
                    .All(same => same);
        }

        public static bool Matches(BodyAppearanceCatalog catalog, BodyAppearanceCatalog.Attachment[] attachments)
        {
            return catalog.Attachments.Count == attachments.Length
                && catalog.Attachments.Zip(attachments, (actual, expected) =>
                    actual.Name == expected.Name
                    && actual.Socket == expected.Socket
                    && actual.Bone == expected.Bone
                    && actual.Address == expected.Address
                    && actual.UseClip == expected.UseClip
                    && actual.HoldsWithBothHands == expected.HoldsWithBothHands
                    && actual.OffHandGrip == expected.OffHandGrip
                    && actual.HoldClip == expected.HoldClip
                    && actual.AttackClip == expected.AttackClip
                    && actual.AttackRelease == expected.AttackRelease
                    && actual.HasLaunchPoint == expected.HasLaunchPoint
                    && actual.LaunchPoint == expected.LaunchPoint
                    && actual.Skeleton == expected.Skeleton
                    && actual.ClipNames.SequenceEqual(expected.ClipNames)
                    && actual.DrawLength == expected.DrawLength)
                    .All(same => same);
        }

        // Every exported body with variants, with a paint layer or a garment that fits it, or with a
        // socket of its skeleton that an attachment is held on.
        public static ArtAssetManifest[] ManifestsWithLooks()
        {
            var painted = ArtAssetManifests.PaintLayers().SelectMany(paint => paint.bodies).ToArray();
            var dressed = ArtAssetManifests.Garments().SelectMany(ArtAssetManifests.BodiesOf).ToArray();
            var holding = ArtAssetManifests.Attachments()
                .SelectMany(attachment => ArtAssetManifests.SocketBones(attachment.socket).Select(socket => socket.Skeleton))
                .ToArray();
            return ArtAssetManifests.ManifestPaths()
                .Select(ArtAssetManifests.Load)
                .Where(manifest => manifest.IsSkinned && manifest.IsExported
                    && (manifest.variants.Length > 0 || painted.Contains(manifest.AssetPath) || dressed.Contains(manifest.AssetPath)
                        || holding.Contains(manifest.skeleton)))
                .ToArray();
        }

        // The attachments a body's sockets can hold, by name: every exported attachment whose socket
        // is a bone of the body's skeleton, with its model, for a tool its use clip and where the
        // other hand holds it, for a weapon its hold and attack clips, the moment its attack
        // releases, where the other hand holds it, and its launch point, and for a skinned model,
        // such as a bow, its skeleton, its clips, and its draw length.
        public static BodyAppearanceCatalog.Attachment[] AttachmentsFor(ArtAssetManifest body)
        {
            return AttachmentsFor(body, out _);
        }

        // Also with the content asset of each attachment: its model and clips.
        public static BodyAppearanceCatalog.Attachment[] AttachmentsFor(ArtAssetManifest body, out PieceSpec[] pieces)
        {
            var found = new List<PieceSpec>();
            var attachments = ArtAssetManifests.Attachments()
                .OrderBy(attachment => attachment.Name, StringComparer.Ordinal)
                .SelectMany(attachment => ArtAssetManifests.SocketBones(attachment.socket)
                    .Where(socket => socket.Skeleton == body.skeleton)
                    .Select(socket =>
                    {
                        var model = AssetDatabase.LoadAssetAtPath<GameObject>(attachment.UnityTargetPath);
                        if (model == null)
                        {
                            throw new InvalidOperationException(attachment.path + ": the model of the attachment is not imported.");
                        }

                        var grip = attachment.offHandGrip ?? Array.Empty<float>();
                        var launch = attachment.launchPoint ?? Array.Empty<float>();
                        var clips = attachment.IsSkinned ? ClipsOf(attachment) : Array.Empty<AnimationClip>();
                        var path = AttachmentContentPath(attachment.Name);
                        found.Add(new PieceSpec
                        {
                            Path = path,
                            Address = ContentAddressOf(path),
                            Model = model,
                            Clips = clips,
                            EndRotations = EndRotationsOf(model, clips),
                        });
                        return new BodyAppearanceCatalog.Attachment(
                            attachment.Name,
                            attachment.socket,
                            socket.Bone,
                            ContentAddressOf(path),
                            attachment.useClip,
                            // In the socket's axes in Blender, which the import turns as it turns the model.
                            grip.Length == 3 ? ArtTopologyMaps.ToUnity(grip[0], grip[1], grip[2]) : (Vector3?)null,
                            attachment.holdClip,
                            attachment.attackClip,
                            launch.Length == 3 ? ArtTopologyMaps.ToUnity(launch[0], launch[1], launch[2]) : (Vector3?)null,
                            attachment.attackRelease,
                            attachment.IsSkinned ? attachment.skeleton : null,
                            clips.Select(clip => clip.name).ToArray(),
                            attachment.drawLength);
                    }))
                .ToArray();
            pieces = found.ToArray();
            return attachments;
        }

        // A clip turns a bone when its end leaves the bone this many degrees or more from its rest.
        public const float EndRotationDegrees = 0.01f;

        // The bones each clip turns, with their local rotations at the clip's end, sampled on a copy
        // of the model in a preview scene, which the Editor can and a player cannot: a player samples
        // no clip outside an Animator. In the order of the clips and then of the model's bones.
        public static BodyPieceContent.EndRotation[] EndRotationsOf(GameObject model, IReadOnlyList<AnimationClip> clips)
        {
            if (model == null || clips == null || clips.Count == 0)
            {
                return Array.Empty<BodyPieceContent.EndRotation>();
            }

            var found = new List<BodyPieceContent.EndRotation>();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var copy = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
                var bones = copy.GetComponentsInChildren<Transform>(true);
                var rest = bones.Select(bone => (bone.localPosition, bone.localRotation, bone.localScale)).ToArray();
                foreach (var clip in clips)
                {
                    if (clip == null)
                    {
                        continue;
                    }

                    clip.SampleAnimation(copy, clip.length);
                    for (var index = 0; index < bones.Length; index++)
                    {
                        if (Quaternion.Angle(rest[index].localRotation, bones[index].localRotation) >= EndRotationDegrees)
                        {
                            found.Add(new BodyPieceContent.EndRotation(clip.name, bones[index].name, bones[index].localRotation));
                        }

                        bones[index].localPosition = rest[index].localPosition;
                        bones[index].localRotation = rest[index].localRotation;
                        bones[index].localScale = rest[index].localScale;
                    }
                }
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }

            return found.ToArray();
        }

        // The clips a skinned attachment exports, imported from its model, in the manifest's order.
        private static AnimationClip[] ClipsOf(ArtAssetManifest attachment)
        {
            var imported = AssetDatabase.LoadAllAssetsAtPath(attachment.UnityTargetPath).OfType<AnimationClip>().ToArray();
            return (attachment.clips ?? Array.Empty<string>())
                .Select(name => imported.FirstOrDefault(clip => clip.name == name)
                    ?? throw new InvalidOperationException(attachment.path + ": the model has no imported clip " + name + "."))
                .ToArray();
        }

        // The editable mesh a body exports next to its model, or null for a body without one.
        public static ArtTopologyMaps.Topology TopologyFor(ArtAssetManifest body)
        {
            var path = body.UnityDataPath;
            var asset = path != null ? AssetDatabase.LoadAssetAtPath<TextAsset>(path) : null;
            if (asset == null)
            {
                return null;
            }

            var topology = ArtBodyTopology.FromJson(asset.text);
            return new ArtTopologyMaps.Topology(topology.vertices, topology.faceSizes, topology.faceVertices);
        }

        // Every part of the body's model that comes from its editable mesh, with the face each of
        // its triangles comes from and the vertex each of its vertices is. The eyes, teeth, and
        // tongue are meshes of their own and no part.
        public static BodyAppearanceCatalog.Part[] PartsFor(ArtAssetManifest body)
        {
            var topology = TopologyFor(body);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(body.UnityTargetPath);
            if (topology == null || model == null)
            {
                return Array.Empty<BodyAppearanceCatalog.Part>();
            }

            var parts = new List<BodyAppearanceCatalog.Part>();
            foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderBy(r => r.name, StringComparer.Ordinal))
            {
                var where = body.UnityTargetPath + " " + renderer.name;
                var candidates = ArtTopologyMaps.VertexCandidates(PositionsIn(model.transform, renderer), topology);
                if (candidates.All(vertex => vertex.Length == 0))
                {
                    continue;
                }

                if (candidates.Any(vertex => vertex.Length == 0))
                {
                    throw new InvalidOperationException(where + " has vertices that are not in the editable mesh; export the body again.");
                }

                var faces = ArtTopologyMaps.TriangleFaces(renderer.sharedMesh, candidates, topology, where);
                parts.Add(new BodyAppearanceCatalog.Part(
                    renderer.name,
                    faces,
                    ArtTopologyMaps.VertexSources(renderer.sharedMesh, candidates, faces, topology)));
            }

            return parts.ToArray();
        }

        // The garments that fit the body, from the lowest layer up: one per exported texture of an
        // exported template that fits the body, named after the texture, with the texture's
        // material and the template's version for the body: its model, and its data with the
        // underlay of every triangle of the model and, per template of a lower layer worn under it,
        // the triangles of that template's model it hides. A texture with transparent parts brings
        // its own data for the body, since its holes change what the body and the garments under it
        // show. A template or a texture whose export has no version for the body, such as one not
        // exported again since the body joined its cage, is left out for that body.
        public static BodyAppearanceCatalog.Garment[] GarmentsFor(ArtAssetManifest body)
        {
            return GarmentsFor(body, out _);
        }

        // Also with the content asset of each garment for the body: the template's model for the
        // body and the texture's material.
        public static BodyAppearanceCatalog.Garment[] GarmentsFor(ArtAssetManifest body, out PieceSpec[] pieces)
        {
            var found = new List<PieceSpec>();
            var label = body.rootObjects.Length == 1 ? body.rootObjects[0] : string.Empty;
            var templates = ArtAssetManifests.Garments()
                .Where(garment => ArtAssetManifests.BodiesOf(garment).Contains(body.AssetPath)
                    && garment.export.models.Any(model => model.body == label))
                .ToDictionary(garment => garment.AssetPath, StringComparer.Ordinal);
            // The face each triangle of a template's model for the body comes from, by template name.
            var triangleFaces = new Dictionary<string, int[]>(StringComparer.Ordinal);
            int[] TriangleFacesOf(ArtAssetManifest garment)
            {
                if (!triangleFaces.TryGetValue(garment.Name, out var faces))
                {
                    faces = TemplateTriangleFaces(garment, label);
                    triangleFaces[garment.Name] = faces;
                }

                return faces;
            }

            var garments = ArtAssetManifests.Textures()
                .Where(texture => templates.ContainsKey(texture.garment)
                    && (texture.export.masks.Length == 0 || texture.export.masks.Any(mask => mask.body == label)))
                .Select(texture => (texture, garment: templates[texture.garment]))
                .OrderBy(pair => pair.garment.layer)
                .ThenBy(pair => pair.texture.Name, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var garment = pair.garment;
                    var material = ArtModelImportRules.FindProjectMaterial(pair.texture.export.materials.Single().name);
                    if (material == null)
                    {
                        throw new InvalidOperationException(pair.texture.path + ": the material of the texture is not created.");
                    }

                    var record = garment.export.models.Single(model => model.body == label);
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(ArtAssetManifests.ToUnityPath(record.target));
                    var data = DataOf(garment, label);
                    var mask = pair.texture.export.masks.SingleOrDefault(entry => entry.body == label);
                    if (mask != null)
                    {
                        var maskText = AssetDatabase.LoadAssetAtPath<TextAsset>(mask.UnityDataPath);
                        if (maskText == null)
                        {
                            throw new InvalidOperationException(pair.texture.path + " " + label + ": the masks of the texture are not imported.");
                        }

                        data = ArtGarmentData.FromJson(maskText.text);
                    }

                    // A texture's masks have the faces of its template.
                    var faces = TriangleFacesOf(garment);
                    var hidden = (data.innerTemplates ?? Array.Empty<string>())
                        .Select(inner => templates.Values.FirstOrDefault(template => template.Name == inner))
                        .Where(inner => inner != null)
                        .Select(inner => HiddenTrianglesFor(inner.Name, TriangleFacesOf(inner), DataOf(inner, label).garmentFaces,
                            data.InnerFaceCountOf(inner.Name), data.HiddenFacesOf(inner.Name), pair.texture.path + " " + label))
                        .ToArray();
                    var path = GarmentContentPath(body, pair.texture.Name);
                    found.Add(new PieceSpec { Path = path, Address = ContentAddressOf(path), Model = model, Material = material });
                    return new BodyAppearanceCatalog.Garment(
                        pair.texture.Name,
                        garment.Name,
                        ContentAddressOf(path),
                        garment.slot,
                        garment.layer,
                        garment.hides,
                        garment.hidesUnderwear,
                        garment.hair,
                        garment.cloth,
                        data.covered,
                        faces.Select(face => data.underlay[face]).ToArray(),
                        data.pullInVertices,
                        data.pullInDistances,
                        hidden);
                })
                .ToArray();
            pieces = found.ToArray();
            return garments;
        }

        // The data a template exports for the body next to its model.
        private static ArtGarmentData DataOf(ArtAssetManifest garment, string label)
        {
            var record = garment.export.models.Single(model => model.body == label);
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(record.UnityDataPath);
            if (text == null)
            {
                throw new InvalidOperationException(garment.path + " " + label + ": the data of the model is not imported.");
            }

            return ArtGarmentData.FromJson(text.text);
        }

        // The face of a template each triangle of its model for the body comes from, over the
        // model's submeshes in order, found by the positions of the faces its data names.
        public static int[] TemplateTriangleFaces(ArtAssetManifest garment, string label)
        {
            var record = garment.export.models.Single(model => model.body == label);
            var where = garment.path + " " + record.body;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ArtAssetManifests.ToUnityPath(record.target));
            if (model == null)
            {
                throw new InvalidOperationException(where + ": the model or its data is not imported.");
            }

            var data = DataOf(garment, label);
            var topology = new ArtTopologyMaps.Topology(data.garmentVertices, data.garmentFaceSizes, data.garmentFaceVertices);
            var renderer = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
            var sources = ArtTopologyMaps.VertexCandidates(PositionsIn(model.transform, renderer), topology);
            if (sources.Any(candidates => candidates.Length == 0))
            {
                throw new InvalidOperationException(where + ": the model has vertices that are not in its data; export the garment again.");
            }

            return ArtTopologyMaps.TriangleFaces(renderer.sharedMesh, sources, topology, where);
        }

        // The triangles a garment hides of a template worn under it: every triangle of the
        // template's model whose face is among the faces the garment's data names, in ascending
        // order and each once, as the mesh plan walks them. The template's model for the body has
        // faceCount faces, which must be the count the data recorded for it, so faces of another
        // export of the template never reach the game.
        public static BodyAppearanceCatalog.HiddenTriangles HiddenTrianglesFor(string template, IReadOnlyList<int> triangleFaces,
            int faceCount, int recordedFaceCount, IReadOnlyList<int> hiddenFaces, string where)
        {
            if (recordedFaceCount != faceCount)
            {
                throw new InvalidOperationException(where + ": the data names the faces it hides of " + template + " with "
                    + recordedFaceCount + " faces, but its model for the body has " + faceCount
                    + "; export the garment again after its templates of lower layers.");
            }

            var hidden = new HashSet<int>(hiddenFaces);
            if (hidden.Any(face => face < 0 || face >= faceCount))
            {
                throw new InvalidOperationException(where + ": the data hides faces that " + template
                    + " does not have; export the garment again after its templates of lower layers.");
            }

            var triangles = new List<int>();
            for (var triangle = 0; triangle < triangleFaces.Count; triangle++)
            {
                if (hidden.Contains(triangleFaces[triangle]))
                {
                    triangles.Add(triangle);
                }
            }

            return new BodyAppearanceCatalog.HiddenTriangles(template, triangles.ToArray());
        }

        // The renderer's vertices in the model root's space.
        private static Vector3[] PositionsIn(Transform root, SkinnedMeshRenderer renderer)
        {
            var toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            return renderer.sharedMesh.vertices.Select(vertex => toRoot.MultiplyPoint3x4(vertex)).ToArray();
        }

        // The paint layers that fit the body, drawn from the lowest layer up: the paint layer
        // Tattoo over M_Body_Male is the image T_Paint_Tattoo_Male, at its crop of the body's paint
        // UVs. Its category in asset.json, such as underwear, is the AppearanceCategory of the same
        // name.
        public static BodyAppearanceCatalog.Paint[] PaintsFor(ArtAssetManifest body)
        {
            return PaintsFor(body, out _);
        }

        // Also with the image of each paint layer, an entry of its own.
        public static BodyAppearanceCatalog.Paint[] PaintsFor(ArtAssetManifest body, out TextureSpec[] textures)
        {
            var found = new List<TextureSpec>();
            var label = body.rootObjects.Length == 1 ? body.rootObjects[0] : string.Empty;
            var paints = ArtAssetManifests.PaintLayers()
                .Where(paint => paint.bodies.Contains(body.AssetPath))
                .OrderBy(paint => paint.layer)
                .ThenBy(paint => paint.Name, StringComparer.Ordinal)
                .Select(paint =>
                {
                    var image = "T_Paint_" + paint.Name + "_" + label;
                    var texture = paint.export.textures.Single(record => record.name == image);
                    var path = ArtAssetManifests.ToUnityPath(texture.target);
                    var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                    if (asset == null)
                    {
                        throw new InvalidOperationException(paint.path + ": the image " + image + " is not imported.");
                    }

                    var address = PaintAddressPrefix + Path.GetFileName(path);
                    found.Add(new TextureSpec { Address = address, Texture = asset });
                    return new BodyAppearanceCatalog.Paint(
                        paint.Name,
                        CategoryOf(paint),
                        paint.slot,
                        ArtModelImportRules.FindProjectMaterial("M_" + paint.part + "_" + label),
                        address,
                        texture.PaintRect,
                        paint.layer,
                        paint.export.smoothness,
                        paint.export.metallic);
                })
                .ToArray();
            textures = found.ToArray();
            return paints;
        }

        public static AppearanceCategory CategoryOf(ArtAssetManifest paint)
        {
            if (!Enum.TryParse(paint.category, true, out AppearanceCategory category)
                || category == AppearanceCategory.Face || category == AppearanceCategory.Eye
                || !string.Equals(category.ToString(), paint.category, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(paint.path + ": the category " + paint.category + " is not underwear, scar, or tattoo.");
            }

            return category;
        }

        public static string PathFor(ArtAssetManifest manifest)
        {
            return Folder + "/" + Path.GetFileNameWithoutExtension(manifest.target) + "_Appearance.asset";
        }

        // M_Face_Male with T_Face_Male_Stubble as its own image and the variant
        // T_Face_Male_Clean is the slot Face with the options Stubble and Clean. The own image is
        // the material's and has no address; every other option's image is an entry of its own.
        public static BodyAppearanceCatalog.Slot[] SlotsFor(ArtAssetManifest manifest)
        {
            return SlotsFor(manifest, out _);
        }

        public static BodyAppearanceCatalog.Slot[] SlotsFor(ArtAssetManifest manifest, out TextureSpec[] textures)
        {
            var found = new List<TextureSpec>();
            var slots = manifest.variants
                .OrderBy(variant => variant.material, StringComparer.Ordinal)
                .Select(variant =>
                {
                    var parts = variant.material.Split('_');
                    if (parts.Length != 3 || parts[0] != "M")
                    {
                        throw new InvalidOperationException(manifest.path + ": " + variant.material + " is not M_<Part>_<Label>.");
                    }

                    var record = manifest.export.materials.Single(material => material.name == variant.material);
                    var prefix = "T_" + parts[1] + "_" + parts[2] + "_";
                    var images = new[] { record.baseMap }.Concat(variant.images);
                    var options = images
                        .Select((image, index) =>
                        {
                            if (index == 0)
                            {
                                return new BodyAppearanceCatalog.Option(image.Substring(prefix.Length), string.Empty);
                            }

                            var path = ArtAssetManifests.ToUnityPath(manifest.export.textures.Single(texture => texture.name == image).target);
                            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                            if (asset == null)
                            {
                                throw new InvalidOperationException(manifest.path + ": the image " + image + " is not imported.");
                            }

                            var address = OptionAddressPrefix + Path.GetFileName(path);
                            found.Add(new TextureSpec { Address = address, Texture = asset });
                            return new BodyAppearanceCatalog.Option(image.Substring(prefix.Length), address);
                        })
                        .ToArray();
                    return new BodyAppearanceCatalog.Slot(
                        parts[1],
                        ArtModelImportRules.FindProjectMaterial(variant.material),
                        options);
                })
                .ToArray();
            textures = found.ToArray();
            return slots;
        }
    }
}
