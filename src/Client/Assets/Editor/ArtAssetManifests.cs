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

        // The manifest whose target is this model, or null.
        public static ArtAssetManifest ForModel(string assetPath)
        {
            return ManifestPaths()
                .Select(Load)
                .FirstOrDefault(manifest => string.Equals(
                    manifest.UnityTargetPath,
                    assetPath,
                    StringComparison.Ordinal));
        }

        public static ArtAssetManifest[] PaintLayers()
        {
            return ManifestPaths()
                .Select(Load)
                .Where(manifest => manifest.IsPaint && manifest.IsExported)
                .ToArray();
        }

        // The label of a body, its armature, such as Male for art/Characters/Human_Male.
        public static string BodyLabel(string bodyAsset)
        {
            var path = Path.Combine(RepositoryLayout.FindRoot(), bodyAsset, "asset.json");
            var body = Load(path);
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
        public ArtAssetExport export = new ArtAssetExport();

        // The paint layer's name, its folder, such as LeatherJerkin.
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

        public string UnityTargetPath
        {
            get { return ArtAssetManifests.ToUnityPath(target); }
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
                    && ((export.materials != null && export.materials.Length > 0)
                        || (IsAnimationLibrary && export.clips != null && export.clips.Length > 0)
                        || (IsPaint && export.textures != null && export.textures.Length > 0));
            }
        }
    }

    [Serializable]
    public sealed class ArtAssetExport
    {
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
    }
}
