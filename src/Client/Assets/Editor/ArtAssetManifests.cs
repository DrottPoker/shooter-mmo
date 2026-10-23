using System;
using System.IO;
using System.Linq;
using Mmorpg.WorldData.Editor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Reads the art/<Category>/<Name>/asset.json manifests and the art/Skeletons definitions
    // that tools/Export-ArtAsset.ps1 writes and checks (see docs/ART_PIPELINE.md). Only the
    // fields the Unity import rules and tests use are read.
    public static class ArtAssetManifests
    {
        public const string SkinnedKind = "skinned";

        // An armature with handmade clips of a skeleton, without meshes.
        public const string AnimationLibraryKind = "animations";

        // Paint over the skin of bodies, exported as one image per body.
        public const string PaintKind = "paint";

        // A garment template, the form of clothes or armor, made on a garment cage and exported as
        // one model per body of the cage, skinned to that body's rig.
        public const string GarmentKind = "garment";

        // The look of one garment template: its image and its material.
        public const string TextureKind = "texture";

        // The net that garment templates are made on, with no export.
        public const string CageKind = "cage";

        // asset.json targets are repository paths; Unity asset paths start at Assets/.
        public const string ClientProjectPrefix = "src/Client/";

        public static string[] ManifestPaths()
        {
            var artRoot = Path.Combine(RepositoryLayout.FindRoot(), "art");
            return Directory.Exists(artRoot)
                ? Directory.GetFiles(artRoot, "asset.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
        }

        public static ArtAssetManifest Load(string manifestPath)
        {
            var manifest = JsonUtility.FromJson<ArtAssetManifest>(File.ReadAllText(manifestPath));
            manifest.path = manifestPath;
            return manifest;
        }

        // The manifest one of whose models is this model, or null.
        public static ArtAssetManifest ForModel(string assetPath)
        {
            return ManifestPaths()
                .Select(Load)
                .FirstOrDefault(manifest => manifest.UnityModelPaths.Contains(assetPath, StringComparer.Ordinal));
        }

        public static ArtAssetManifest[] Garments()
        {
            return ManifestPaths()
                .Select(Load)
                .Where(manifest => manifest.IsGarment && manifest.IsExported)
                .ToArray();
        }

        // The exported garment textures, each the look of one template.
        public static ArtAssetManifest[] Textures()
        {
            return ManifestPaths()
                .Select(Load)
                .Where(manifest => manifest.IsTexture && manifest.IsExported)
                .ToArray();
        }

        // The bodies a garment template fits: those of its cage.
        public static string[] BodiesOf(ArtAssetManifest garment)
        {
            return string.IsNullOrEmpty(garment.cage) ? garment.bodies : LoadAsset(garment.cage).bodies;
        }

        public static ArtAssetManifest[] PaintLayers()
        {
            return ManifestPaths()
                .Select(Load)
                .Where(manifest => manifest.IsPaint && manifest.IsExported)
                .ToArray();
        }

        // The manifest of an asset folder, such as art/Characters/Human_Male.
        public static ArtAssetManifest LoadAsset(string asset)
        {
            return Load(Path.Combine(RepositoryLayout.FindRoot(), asset, "asset.json"));
        }

        // The label of a body, its armature, such as Male for art/Characters/Human_Male.
        public static string BodyLabel(string bodyAsset)
        {
            var body = LoadAsset(bodyAsset);
            return body.rootObjects.Length == 1 ? body.rootObjects[0] : string.Empty;
        }

        // The materials that paint layers paint, M_<Part>_<Label>, which draw with the skin shader.
        public static string[] PaintedMaterialNames()
        {
            return PaintLayers()
                .SelectMany(paint => paint.bodies.Select(body => "M_" + paint.part + "_" + BodyLabel(body)))
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        // The materials of exported garment templates and textures, M_Garment_<Name>, which draw
        // with the garment shader.
        public static string[] GarmentMaterialNames()
        {
            return Garments().Concat(Textures())
                .SelectMany(garment => garment.export.materials.Select(material => material.name))
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }

        public static ArtSkeleton LoadSkeleton(string name)
        {
            var path = Path.Combine(RepositoryLayout.FindRoot(), "art", "Skeletons", name + ".json");
            return JsonUtility.FromJson<ArtSkeleton>(File.ReadAllText(path));
        }

        // The Unity path of a repository path below src/Client, or null.
        public static string ToUnityPath(string repositoryPath)
        {
            return repositoryPath != null && repositoryPath.StartsWith(ClientProjectPrefix, StringComparison.Ordinal)
                ? repositoryPath.Substring(ClientProjectPrefix.Length)
                : null;
        }
    }

    [Serializable]
    public sealed class ArtAssetManifest
    {
        [NonSerialized] public string path = string.Empty;
        public string kind = string.Empty;
        public string target = string.Empty;
        public string[] rootObjects = Array.Empty<string>();
        public string skeleton = string.Empty;
        public string[] clips = Array.Empty<string>();
        public ArtMaterialVariants[] variants = Array.Empty<ArtMaterialVariants>();
        // A paint layer: the painted material part, the bodies, the painted one first, and the
        // order paint layers are drawn in.
        public string part = string.Empty;
        public string[] bodies = Array.Empty<string>();
        public int layer;
        // A garment template: the cage it is made on, whose bodies it fits; its visual slot and its
        // band from the cage; its layer from the inside out; the slots it hides; what headwear does
        // to the hair; and the cloth profile of its loose parts.
        public string cage = string.Empty;
        public string slot = string.Empty;
        public string band = string.Empty;
        public string[] hides = Array.Empty<string>();
        public string hair = string.Empty;
        public string cloth = string.Empty;
        // A garment texture: the template it belongs to, such as art/Garments/Jerkin.
        public string garment = string.Empty;
        public ArtAssetExport export = new ArtAssetExport();

        // The asset's name, its folder, such as LeatherJerkin.
        public string Name
        {
            get { return Path.GetFileName(Path.GetDirectoryName(path)); }
        }

        // The repository path of the asset folder, such as art/Characters/Human_Male.
        public string AssetPath
        {
            get
            {
                var root = RepositoryLayout.FindRoot();
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                return Path.GetRelativePath(root, directory).Replace('\\', '/');
            }
        }

        public bool IsPaint
        {
            get { return string.Equals(kind, ArtAssetManifests.PaintKind, StringComparison.Ordinal); }
        }

        public bool IsGarment
        {
            get { return string.Equals(kind, ArtAssetManifests.GarmentKind, StringComparison.Ordinal); }
        }

        public bool IsTexture
        {
            get { return string.Equals(kind, ArtAssetManifests.TextureKind, StringComparison.Ordinal); }
        }

        public string UnityTargetPath
        {
            get { return ArtAssetManifests.ToUnityPath(target); }
        }

        // A body with an editable mesh exports its topology next to its model, or null.
        public string UnityDataPath
        {
            get
            {
                return IsSkinned && export != null && !string.IsNullOrEmpty(export.dataSha256) && UnityTargetPath != null
                    ? Path.ChangeExtension(UnityTargetPath, ".json")
                    : null;
            }
        }

        // The Unity paths of the asset's exported models: its target, or a garment's model per body.
        public string[] UnityModelPaths
        {
            get
            {
                if (IsGarment)
                {
                    return (export?.models ?? Array.Empty<ArtModelRecord>())
                        .Select(model => ArtAssetManifests.ToUnityPath(model.target))
                        .Where(modelPath => modelPath != null)
                        .ToArray();
                }

                return UnityTargetPath != null ? new[] { UnityTargetPath } : Array.Empty<string>();
            }
        }

        // The root objects of one of its models: a garment's model holds its body's armature.
        public string[] RootObjectsOf(string unityModelPath)
        {
            if (!IsGarment)
            {
                return rootObjects;
            }

            var model = (export?.models ?? Array.Empty<ArtModelRecord>())
                .FirstOrDefault(record => ArtAssetManifests.ToUnityPath(record.target) == unityModelPath);
            return model != null ? new[] { model.body } : Array.Empty<string>();
        }

        public bool IsSkinned
        {
            get { return string.Equals(kind, ArtAssetManifests.SkinnedKind, StringComparison.Ordinal); }
        }

        public bool IsAnimationLibrary
        {
            get { return string.Equals(kind, ArtAssetManifests.AnimationLibraryKind, StringComparison.Ordinal); }
        }

        // A skinned model or an animation library: a Humanoid armature with clips.
        public bool HasSkeleton
        {
            get { return IsSkinned || IsAnimationLibrary; }
        }

        // JsonUtility reads "export": null as an empty record. Every model has materials, and
        // an animation library has clips.
        public bool IsExported
        {
            get
            {
                return export != null
                    && ((export.materials != null && export.materials.Length > 0
                            && (!IsGarment || (export.models != null && export.models.Length > 0)))
                        || (IsAnimationLibrary && export.clips != null && export.clips.Length > 0)
                        || (IsPaint && export.textures != null && export.textures.Length > 0));
            }
        }
    }

    [Serializable]
    public sealed class ArtAssetExport
    {
        // A body's topology next to its model.
        public string dataSha256 = string.Empty;
        // A garment's model per body.
        public ArtModelRecord[] models = Array.Empty<ArtModelRecord>();
        // A garment texture with transparent parts: its own masks per body of its template.
        public ArtMaskRecord[] masks = Array.Empty<ArtMaskRecord>();
        public ArtMaterialRecord[] materials = Array.Empty<ArtMaterialRecord>();
        public ArtTextureRecord[] textures = Array.Empty<ArtTextureRecord>();
        public ArtClipRecord[] clips = Array.Empty<ArtClipRecord>();
        // A paint layer's smoothness and metallic.
        public float smoothness = -1f;
        public float metallic = -1f;
    }

    [Serializable]
    public sealed class ArtMaterialRecord
    {
        public string name = string.Empty;
        public string baseColor = string.Empty;
        public string baseMap = string.Empty;
        public float metallic = -1f;
        public float smoothness = -1f;
    }

    // A garment's model for one body: the body's label, such as Male, the model, and its data.
    [Serializable]
    public sealed class ArtModelRecord
    {
        public string body = string.Empty;
        public string target = string.Empty;
        public string sha256 = string.Empty;
        public string data = string.Empty;
        public string dataSha256 = string.Empty;

        public string UnityDataPath
        {
            get { return ArtAssetManifests.ToUnityPath(data); }
        }
    }

    // The masks of a garment texture with transparent parts on one body, in the data format of its
    // template's.
    [Serializable]
    public sealed class ArtMaskRecord
    {
        public string body = string.Empty;
        public string data = string.Empty;
        public string dataSha256 = string.Empty;

        public string UnityDataPath
        {
            get { return ArtAssetManifests.ToUnityPath(data); }
        }
    }

    // What a garment's model covers on its body, in the faces and vertices of the body's editable
    // mesh, which every body of the shared topology numbers alike: the body faces it hides, the
    // body face under each of its faces, and how far to move visible body vertices inward.
    // Export-ArtAsset.ps1 writes it next to the model; see docs/EQUIPMENT_VISUALS_DESIGN.md.
    [Serializable]
    public sealed class ArtGarmentData
    {
        public const int SchemaVersion = 2;

        // The most a pulled-in vertex moves, in meters.
        public const float MaximumPullIn = 0.003f;

        public int schemaVersion;
        public string garment = string.Empty;
        public string body = string.Empty;
        public int bodyFaces;
        public int bodyVertices;
        public int garmentFaces;
        public int[] covered = Array.Empty<int>();
        public int[] underlay = Array.Empty<int>();
        public int[] pullInVertices = Array.Empty<int>();
        public float[] pullInDistances = Array.Empty<float>();
        // The garment's own vertices in Blender's world space and its faces, so the faces of the
        // model's triangles can be found.
        public float[] garmentVertices = Array.Empty<float>();
        public int[] garmentFaceSizes = Array.Empty<int>();
        public int[] garmentFaceVertices = Array.Empty<int>();

        public static ArtGarmentData FromJson(string json)
        {
            return JsonUtility.FromJson<ArtGarmentData>(json);
        }
    }

    // A body's editable mesh: its vertices in Blender's world space, its faces, and the body part
    // of each face. Export-ArtAsset.ps1 writes it next to the body's model.
    [Serializable]
    public sealed class ArtBodyTopology
    {
        public const int SchemaVersion = 1;

        public int schemaVersion;
        public string body = string.Empty;
        public float[] vertices = Array.Empty<float>();
        public int[] faceSizes = Array.Empty<int>();
        public int[] faceVertices = Array.Empty<int>();
        public int[] faceZones = Array.Empty<int>();

        public static ArtBodyTopology FromJson(string json)
        {
            return JsonUtility.FromJson<ArtBodyTopology>(json);
        }
    }

    [Serializable]
    public sealed class ArtTextureRecord
    {
        public string name = string.Empty;
        public string target = string.Empty;
        public string sha256 = string.Empty;
    }

    // A painted material M_<Part>_<Label> and the other images T_<Part>_<Label>_<Option> it may
    // show in the game instead of its own.
    [Serializable]
    public sealed class ArtMaterialVariants
    {
        public string material = string.Empty;
        public string[] images = Array.Empty<string>();
    }

    [Serializable]
    public sealed class ArtClipRecord
    {
        public string name = string.Empty;
        public int frames;
        public bool loop;
    }

    [Serializable]
    public sealed class ArtSkeleton
    {
        public string name = string.Empty;
        public ArtSkeletonBone[] bones = Array.Empty<ArtSkeletonBone>();
    }

    [Serializable]
    public sealed class ArtSkeletonBone
    {
        public string name = string.Empty;
        public string parent = string.Empty;
        // The HumanBodyBones name of the bone, empty for a bone outside the Humanoid avatar.
        public string human = string.Empty;
        public bool deform;
        // The cloth chain of the bone, such as skirt, cloak, or hood, empty for every other bone.
        public string chain = string.Empty;
    }
}
