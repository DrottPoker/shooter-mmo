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
        public ArtAssetExport export = new ArtAssetExport();

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
                        || (IsAnimationLibrary && export.clips != null && export.clips.Length > 0));
            }
        }
    }

    [Serializable]
    public sealed class ArtAssetExport
    {
        public ArtMaterialRecord[] materials = Array.Empty<ArtMaterialRecord>();
        public ArtTextureRecord[] textures = Array.Empty<ArtTextureRecord>();
        public ArtClipRecord[] clips = Array.Empty<ArtClipRecord>();
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
