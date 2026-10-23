using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Import rules for every model that tools/Export-ArtAsset.ps1 writes below
    // Assets/Art/Models and every texture it writes below Assets/Art/Textures (see
    // docs/ART_PIPELINE.md). The settings are forced on each import, so no asset depends on
    // hand-edited importer settings. Materials keep the names they have in Blender and are
    // remapped to the project material of the same name below Assets/Art/Materials; a model
    // without a matching material keeps an embedded one, which the EditMode tests reject.
    // A skinned model and an animation library get a Humanoid avatar mapped from their skeleton
    // definition and one animation clip per exported clip, as asset.json records them. A garment's
    // model is skinned to its body's armature without an avatar of its own, because the character
    // it is put on animates it.
    public sealed class ArtModelImportRules : AssetPostprocessor
    {
        public const string ModelFolder = "Assets/Art/Models";
        public const string MaterialFolder = "Assets/Art/Materials";
        public const string TextureFolder = "Assets/Art/Textures";

        // Paint layers keep their alpha, which says where the paint covers the skin, and garments
        // theirs, which cuts holes, lace, and torn edges.
        public const string PaintTextureFolder = TextureFolder + "/Paint";
        public const string GarmentTextureFolder = TextureFolder + "/Garments";
        public const int MaximumTextureSize = 2048;

        // Blender writes every clip at this rate; the animation rules require it.
        public const float ClipFrameRate = 30f;

        public override uint GetVersion()
        {
            return 7;
        }

        public static bool AppliesTo(string assetPath)
        {
            return assetPath.StartsWith(ModelFolder + "/", StringComparison.Ordinal)
                && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        public static bool AppliesToTexture(string assetPath)
        {
            return assetPath.StartsWith(TextureFolder + "/", StringComparison.Ordinal)
                && assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        }

        // The data an export writes next to a model: a body's topology or a garment's masks.
        public static bool AppliesToData(string assetPath)
        {
            return assetPath.StartsWith(ModelFolder + "/", StringComparison.Ordinal)
                && assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        public static void ApplyStaticModelSettings(ModelImporter importer)
        {
            ApplySharedModelSettings(importer);
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
        }

        public static void ApplySkinnedModelSettings(ModelImporter importer, ArtAssetManifest manifest, ArtSkeleton skeleton)
        {
            ApplyHumanoidSettings(importer, manifest, skeleton);
            // The game combines the visible body parts into one mesh, which reads the parts.
            importer.isReadable = true;
        }

        // A garment's model: its mesh skinned to the armature of its body, which the game replaces
        // with the character's own skeleton, so it needs no avatar and no clips. The game combines
        // it with the visible body parts, which reads it.
        public static void ApplyGarmentModelSettings(ModelImporter importer)
        {
            ApplySharedModelSettings(importer);
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
            importer.importAnimation = false;
            importer.optimizeGameObjects = false;
            importer.isReadable = true;
        }

        // An animation library has an armature and clips, without meshes. Its avatar is made
        // from its own armature, so its clips become muscle motion that plays on every body of
        // the skeleton.
        public static void ApplyAnimationLibrarySettings(ModelImporter importer, ArtAssetManifest manifest, ArtSkeleton skeleton)
        {
            ApplyHumanoidSettings(importer, manifest, skeleton);
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
        }

        private static void ApplyHumanoidSettings(ModelImporter importer, ArtAssetManifest manifest, ArtSkeleton skeleton)
        {
            ApplySharedModelSettings(importer);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.humanDescription = HumanDescriptionFor(skeleton);
            // The sockets must stay transforms, so equipment can be attached to them.
            importer.optimizeGameObjects = false;
            var clips = ClipsFor(manifest);
            // A body without clips of its own plays the clips of animation libraries.
            importer.importAnimation = clips.Length > 0;
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            importer.clipAnimations = clips;
        }

        // Maps every bone of the skeleton definition that names a HumanBodyBones bone, with
        // Unity's default limits, twist, and stretch.
        public static HumanDescription HumanDescriptionFor(ArtSkeleton skeleton)
        {
            return new HumanDescription
            {
                human = skeleton.bones
                    .Where(bone => !string.IsNullOrEmpty(bone.human))
                    .Select(bone => new HumanBone
                    {
                        boneName = bone.name,
                        humanName = HumanTrait.BoneName[(int)Enum.Parse<HumanBodyBones>(bone.human)],
                        limit = new HumanLimit { useDefaultValues = true },
                    })
                    .ToArray(),
                skeleton = Array.Empty<SkeletonBone>(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };
        }

        // Blender names each take <armature>|<action>. Every clip plays in place: the body's
        // turn, height, and sway stay in the pose, and the game moves the character.
        public static ModelImporterClipAnimation[] ClipsFor(ArtAssetManifest manifest)
        {
            var armature = manifest.rootObjects.Length > 0 ? manifest.rootObjects[0] : string.Empty;
            return (manifest.export?.clips ?? Array.Empty<ArtClipRecord>())
                .Select(clip => new ModelImporterClipAnimation
                {
                    name = clip.name,
                    takeName = armature + "|" + clip.name,
                    firstFrame = 0f,
                    lastFrame = clip.frames,
                    loopTime = clip.loop,
                    // The last frame already repeats the first.
                    loopPose = false,
                    lockRootRotation = true,
                    keepOriginalOrientation = true,
                    lockRootHeightY = true,
                    keepOriginalPositionY = true,
                    lockRootPositionXZ = true,
                    keepOriginalPositionXZ = true,
                })
                .ToArray();
        }

        public static bool IsPaintTexture(string assetPath)
        {
            return assetPath.StartsWith(PaintTextureFolder + "/", StringComparison.Ordinal);
        }

        public static bool IsGarmentTexture(string assetPath)
        {
            return assetPath.StartsWith(GarmentTextureFolder + "/", StringComparison.Ordinal);
        }

        // Whether a texture's alpha is used: a paint layer's or a garment's.
        public static bool KeepsAlpha(string assetPath)
        {
            return IsPaintTexture(assetPath) || IsGarmentTexture(assetPath);
        }

        public static void ApplyTextureSettings(TextureImporter importer, bool alpha = false)
        {
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = true;
            // Transparent texels take the color of their neighbors, so filtering at the edges of a
            // paint layer never darkens the skin and the cut edges of a garment keep its color.
            importer.alphaSource = alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = alpha;
            importer.mipmapEnabled = true;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.maxTextureSize = MaximumTextureSize;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.crunchedCompression = false;
        }

        public static Material FindProjectMaterial(string name)
        {
            var matches = AssetDatabase.FindAssets("t:Material " + name, new[] { MaterialFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => string.Equals(
                    System.IO.Path.GetFileNameWithoutExtension(path), name, StringComparison.Ordinal))
                .ToArray();
            return matches.Length == 1 ? AssetDatabase.LoadAssetAtPath<Material>(matches[0]) : null;
        }

        private static void ApplySharedModelSettings(ModelImporter importer)
        {
            // Blender exports in meters with its own Z-up axes; baking the axis conversion
            // maps Blender +Y to Unity +Z and leaves every object with the rotation it has in
            // Blender. An explicit root keeps every Blender root object a direct child of the
            // model, also when there is only one.
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.preserveHierarchy = true;
            importer.importCameras = false;
            importer.importLights = false;
            // Objects hidden in Blender, such as a Depleted state, must still import enabled.
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            // Presentation never takes part in physics; gameplay colliders come from the server.
            importer.addCollider = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.generateSecondaryUV = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        private void OnPreprocessModel()
        {
            if (!AppliesTo(assetPath))
            {
                return;
            }

            var importer = (ModelImporter)assetImporter;
            var manifest = ArtAssetManifests.ForModel(assetPath);
            if (manifest != null && manifest.IsSkinned)
            {
                ApplySkinnedModelSettings(importer, manifest, ArtAssetManifests.LoadSkeleton(manifest.skeleton));
            }
            else if (manifest != null && manifest.IsAnimationLibrary)
            {
                ApplyAnimationLibrarySettings(importer, manifest, ArtAssetManifests.LoadSkeleton(manifest.skeleton));
            }
            else if (manifest != null && manifest.IsGarment)
            {
                ApplyGarmentModelSettings(importer);
            }
            else
            {
                ApplyStaticModelSettings(importer);
            }
        }

        private void OnPreprocessTexture()
        {
            if (AppliesToTexture(assetPath))
            {
                ApplyTextureSettings((TextureImporter)assetImporter, KeepsAlpha(assetPath));
            }
        }

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets.Where(AppliesTo))
            {
                RemapEmbeddedMaterials(path);
            }
        }

        private static void RemapEmbeddedMaterials(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
            {
                return;
            }

            var remapped = false;
            foreach (var embedded in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
            {
                var projectMaterial = FindProjectMaterial(embedded.name);
                if (projectMaterial != null)
                {
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(embedded), projectMaterial);
                    remapped = true;
                }
            }

            // The reimport has no embedded material left for these names, so this runs once.
            if (remapped)
            {
                importer.SaveAndReimport();
            }
        }
    }
}
