using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mmorpg.Client.Tests.EditMode
{
    // Models below Assets/Art/Models and textures below Assets/Art/Textures come from
    // tools/Export-ArtAsset.ps1 and must import with ArtModelImportRules; see
    // docs/ART_PIPELINE.md.
    public sealed class ArtModelImportTests
    {
        private static string[] ModelPaths()
        {
            return AssetDatabase.FindAssets("t:Model", new[] { ArtModelImportRules.ModelFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(ArtModelImportRules.AppliesTo)
                .ToArray();
        }

        private static ArtAssetManifest[] ExportedManifests()
        {
            return ArtAssetManifests.ManifestPaths()
                .Select(ArtAssetManifests.Load)
                .Where(manifest => manifest.IsExported)
                .ToArray();
        }

        [Test]
        public void EveryArtModelUsesTheImportRulesOfItsKind()
        {
            var paths = ModelPaths();
            Assert.That(paths, Is.Not.Empty);
            foreach (var path in paths)
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                var manifest = ArtAssetManifests.ForModel(path);
                Assert.That(manifest, Is.Not.Null, path + " has no asset.json");
                Assert.That(importer.globalScale, Is.EqualTo(1f), path);
                Assert.That(importer.useFileScale, Is.True, path);
                Assert.That(importer.bakeAxisConversion, Is.True, path);
                Assert.That(importer.preserveHierarchy, Is.True, path);
                Assert.That(importer.importCameras, Is.False, path);
                Assert.That(importer.importLights, Is.False, path);
                Assert.That(importer.importVisibility, Is.False, path);
                Assert.That(importer.importBlendShapes, Is.False, path);
                Assert.That(importer.addCollider, Is.False, path);
                Assert.That(importer.isReadable, Is.EqualTo(manifest.IsSkinned || manifest.IsGarment), path);
                Assert.That(importer.importNormals, Is.EqualTo(ModelImporterNormals.Import), path);
                if (manifest.IsGarment)
                {
                    // The character's skeleton animates a garment, so it has no avatar or clips.
                    Assert.That(importer.importAnimation, Is.False, path);
                    Assert.That(importer.animationType, Is.EqualTo(ModelImporterAnimationType.Generic), path);
                    Assert.That(importer.avatarSetup, Is.EqualTo(ModelImporterAvatarSetup.NoAvatar), path);
                    Assert.That(importer.optimizeGameObjects, Is.False, path);
                }
                else if (manifest.HasSkeleton)
                {
                    // A body without clips of its own plays the clips of animation libraries.
                    Assert.That(importer.importAnimation, Is.EqualTo(manifest.clips.Length > 0), path);
                    Assert.That(importer.animationType, Is.EqualTo(ModelImporterAnimationType.Human), path);
                    Assert.That(importer.avatarSetup, Is.EqualTo(ModelImporterAvatarSetup.CreateFromThisModel), path);
                    Assert.That(importer.optimizeGameObjects, Is.False, path);
                }
                else
                {
                    Assert.That(importer.importAnimation, Is.False, path);
                    Assert.That(importer.animationType, Is.EqualTo(ModelImporterAnimationType.None), path);
                }
            }
        }

        [Test]
        public void EverySkinnedModelHasAHumanoidAvatarFromItsSkeleton()
        {
            var skinned = ExportedManifests().Where(manifest => manifest.HasSkeleton).ToArray();
            Assert.That(skinned.Where(manifest => manifest.IsSkinned), Is.Not.Empty);
            Assert.That(skinned.Where(manifest => manifest.IsAnimationLibrary), Is.Not.Empty);
            foreach (var manifest in skinned)
            {
                var path = manifest.UnityTargetPath;
                var skeleton = ArtAssetManifests.LoadSkeleton(manifest.skeleton);
                var avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().Single();
                Assert.That(avatar.isValid, Is.True, path);
                Assert.That(avatar.isHuman, Is.True, path);

                // Every bone of the definition that names a human bone maps to it.
                var mapped = avatar.humanDescription.human.ToDictionary(bone => bone.humanName, bone => bone.boneName);
                var human = skeleton.bones.Where(bone => !string.IsNullOrEmpty(bone.human)).ToArray();
                Assert.That(mapped.Count, Is.EqualTo(human.Length), path);
                foreach (var bone in human)
                {
                    var humanName = HumanTrait.BoneName[(int)Enum.Parse<HumanBodyBones>(bone.human)];
                    Assert.That(mapped.TryGetValue(humanName, out var boneName), Is.True, path + " " + humanName);
                    Assert.That(boneName, Is.EqualTo(bone.name), path + " " + humanName);
                }

                // Every bone stays a transform under its parent, also the sockets.
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var transforms = model.GetComponentsInChildren<Transform>(true)
                    .GroupBy(transform => transform.name)
                    .ToDictionary(group => group.Key, group => group.ToArray());
                foreach (var bone in skeleton.bones)
                {
                    Assert.That(transforms.TryGetValue(bone.name, out var found), Is.True, path + " " + bone.name);
                    Assert.That(found, Has.Length.EqualTo(1), path + " " + bone.name);
                    if (!string.IsNullOrEmpty(bone.parent))
                    {
                        Assert.That(found[0].parent.name, Is.EqualTo(bone.parent), path + " " + bone.name);
                    }
                }
            }
        }

        [Test]
        public void EverySkinnedModelHasTheExportedClips()
        {
            foreach (var manifest in ExportedManifests().Where(manifest => manifest.HasSkeleton))
            {
                var path = manifest.UnityTargetPath;
                Assert.That(manifest.export.clips.Select(clip => clip.name), Is.EquivalentTo(manifest.clips), path);
                var clips = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<AnimationClip>()
                    .Where(clip => !clip.name.StartsWith("__preview", StringComparison.Ordinal))
                    .ToDictionary(clip => clip.name);
                Assert.That(clips.Keys, Is.EquivalentTo(manifest.clips), path);
                foreach (var record in manifest.export.clips)
                {
                    var clip = clips[record.name];
                    var where = path + " " + record.name;
                    Assert.That(clip.isHumanMotion, Is.True, where);
                    Assert.That(clip.isLooping, Is.EqualTo(record.loop), where);
                    Assert.That(clip.length, Is.EqualTo(record.frames / ArtModelImportRules.ClipFrameRate).Within(0.001f), where);
                }
            }
        }

        [Test]
        public void EveryGarmentModelIsSkinnedToTheRigOfItsBody()
        {
            // A garment deforms like the body it was fitted to: its armature is the body's, and its
            // bones have the body's bind poses, so the character's skeleton moves it with the body.
            foreach (var manifest in ExportedManifests().Where(manifest => manifest.IsGarment))
            {
                var bodies = ArtAssetManifests.BodiesOf(manifest);
                Assert.That(
                    manifest.export.models.Select(model => model.body),
                    Is.EqualTo(bodies.Select(ArtAssetManifests.BodyLabel)),
                    manifest.path);
                foreach (var record in manifest.export.models)
                {
                    var path = ArtAssetManifests.ToUnityPath(record.target);
                    var garment = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    Assert.That(garment, Is.Not.Null, path);
                    var body = ArtAssetManifests.LoadAsset(
                        bodies.Single(asset => ArtAssetManifests.BodyLabel(asset) == record.body));
                    var skeleton = ArtAssetManifests.LoadSkeleton(body.skeleton);
                    var armature = garment.transform.Find(record.body);
                    Assert.That(armature, Is.Not.Null, path + " lacks the armature " + record.body);
                    var bones = armature.GetComponentsInChildren<Transform>(true).Select(bone => bone.name).ToArray();
                    Assert.That(skeleton.bones.Select(bone => bone.name), Is.SubsetOf(bones), path);

                    var renderers = garment.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    Assert.That(renderers.Select(renderer => renderer.name), Is.EqualTo(new[] { manifest.Name + "_" + record.body }), path);
                    var mesh = renderers[0].sharedMesh;
                    Assert.That(mesh.isReadable, Is.True, path);
                    Assert.That(mesh.GetBonesPerVertex().ToArray().All(count => count >= 1 && count <= 4), Is.True, path);

                    var bindposes = new Dictionary<string, Matrix4x4>();
                    foreach (var part in AssetDatabase.LoadAssetAtPath<GameObject>(body.UnityTargetPath)
                                 .GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        for (var i = 0; i < part.bones.Length; i++)
                        {
                            bindposes[part.bones[i].name] = part.sharedMesh.bindposes[i];
                        }
                    }

                    for (var i = 0; i < renderers[0].bones.Length; i++)
                    {
                        var bone = renderers[0].bones[i];
                        var where = path + " " + bone.name;
                        Assert.That(bindposes.TryGetValue(bone.name, out var expected), Is.True, where);
                        var actual = mesh.bindposes[i];
                        for (var cell = 0; cell < 16; cell++)
                        {
                            Assert.That(actual[cell], Is.EqualTo(expected[cell]).Within(1e-4f), where);
                        }
                    }
                }
            }
        }

        [Test]
        public void EveryGarmentModelHasItsData()
        {
            // The data says which body faces the garment hides and which face lies under each of its
            // faces, in the numbering of the body's editable mesh. A texture with transparent parts
            // has data of its own per body of its template.
            var garments = ExportedManifests().Where(manifest => manifest.IsGarment).ToArray();
            Assert.That(garments, Is.Not.Empty);
            var entries = garments
                .SelectMany(manifest => manifest.export.models.Select(record => (manifest, record.body, record.data, path: record.UnityDataPath)))
                .Concat(ExportedManifests().Where(manifest => manifest.IsTexture)
                    .SelectMany(manifest => manifest.export.masks.Select(record => (manifest, record.body, record.data, path: record.UnityDataPath))));
            foreach (var (manifest, body, file, path) in entries)
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                Assert.That(asset, Is.Not.Null, manifest.path + " " + file);
                // A new import of the data alone updates the appearance catalogs.
                Assert.That(ArtModelImportRules.AppliesToData(path), Is.True, path);
                var data = ArtGarmentData.FromJson(asset.text);
                Assert.That(data.schemaVersion, Is.EqualTo(ArtGarmentData.SchemaVersion), path);
                Assert.That(data.garment, Is.EqualTo(manifest.Name), path);
                Assert.That(data.body, Is.EqualTo(body), path);
                Assert.That(data.bodyFaces, Is.GreaterThan(0), path);
                // A template covers the body; a texture's holes, such as a net's meshes, may leave
                // every body face visible.
                if (manifest.IsGarment)
                {
                    Assert.That(data.covered, Is.Not.Empty, path);
                }

                Assert.That(data.covered, Is.Ordered.And.Unique, path);
                Assert.That(data.covered.All(face => face >= 0 && face < data.bodyFaces), Is.True, path);
                Assert.That(data.underlay, Has.Length.EqualTo(data.garmentFaces), path);
                // The garment's own faces, so the catalog finds the face of every triangle.
                Assert.That(data.garmentFaceSizes, Has.Length.EqualTo(data.garmentFaces), path);
                Assert.That(data.garmentFaceVertices, Has.Length.EqualTo(data.garmentFaceSizes.Sum()), path);
                Assert.That(data.garmentVertices.Length % 3, Is.Zero, path);
                Assert.That(data.underlay.All(face => face >= 0 && face < data.bodyFaces), Is.True, path);
                Assert.That(data.pullInDistances, Has.Length.EqualTo(data.pullInVertices.Length), path);
                Assert.That(data.pullInVertices, Is.Ordered.And.Unique, path);
                Assert.That(data.pullInVertices.All(vertex => vertex >= 0 && vertex < data.bodyVertices), Is.True, path);
                Assert.That(
                    data.pullInDistances.All(distance => distance > 0f && distance <= ArtGarmentData.MaximumPullIn + 1e-6f),
                    Is.True,
                    path);
            }
        }

        [Test]
        public void EveryArtModelUsesProjectMaterials()
        {
            foreach (var path in ModelPaths())
            {
                Assert.That(
                    AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Select(m => m.name),
                    Is.Empty,
                    path + " embeds materials; name them in Blender like a material in "
                        + ArtModelImportRules.MaterialFolder);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var renderers = model.GetComponentsInChildren<Renderer>(true);
                // An animation library is an armature and its clips; the bodies stay in Blender.
                if (ArtAssetManifests.ForModel(path).IsAnimationLibrary)
                {
                    Assert.That(renderers, Is.Empty, path);
                    continue;
                }

                Assert.That(renderers, Is.Not.Empty, path);
                foreach (var renderer in renderers)
                {
                    Assert.That(renderer.sharedMaterials, Is.Not.Empty, path + " " + renderer.name);
                    foreach (var material in renderer.sharedMaterials)
                    {
                        Assert.That(material, Is.Not.Null, path + " " + renderer.name);
                        Assert.That(
                            AssetDatabase.GetAssetPath(material),
                            Does.StartWith(ArtModelImportRules.MaterialFolder + "/"),
                            path + " " + renderer.name);
                    }
                }
            }
        }

        [Test]
        public void EveryArtMaterialMatchesTheApprovedBlenderMaterial()
        {
            // The export records the approved Blender look of every material in asset.json, so
            // the game shows the colors and the painted textures the reviewer approved.
            var manifests = ArtAssetManifests.ManifestPaths().Select(ArtAssetManifests.Load).ToArray();
            Assert.That(manifests, Is.Not.Empty);
            foreach (var manifest in manifests.Where(manifest => !manifest.IsPaint))
            {
                // A paint layer exports images only; ArtAppearanceCatalogTests covers it.
                if (!manifest.IsExported)
                {
                    // An asset in review, such as a skinned model before its rig, is not exported yet.
                    var models = manifest.IsGarment
                        ? ModelPaths().Where(path => path.StartsWith(
                            ArtModelImportRules.ModelFolder + "/Garments/" + manifest.Name + "_", StringComparison.Ordinal))
                        : ModelPaths().Where(path => path == manifest.UnityTargetPath);
                    Assert.That(models, Is.Empty, manifest.path + " has a model but no export record");
                    continue;
                }

                // Every material the models render with has an approved record, and no record is stale.
                // A garment texture has no model: the catalog draws its template's models with it.
                Assert.That(manifest.UnityModelPaths, manifest.IsTexture ? Is.Empty : Is.Not.Empty, manifest.path);
                foreach (var modelPath in manifest.UnityModelPaths)
                {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                    Assert.That(model, Is.Not.Null, modelPath);
                    Assert.That(
                        model.GetComponentsInChildren<Renderer>(true)
                            .SelectMany(renderer => renderer.sharedMaterials)
                            .Select(material => material.name)
                            .Distinct()
                            .OrderBy(name => name, StringComparer.Ordinal),
                        Is.EqualTo(manifest.export.materials
                            .Select(record => record.name)
                            .OrderBy(name => name, StringComparer.Ordinal)),
                        modelPath);
                }

                foreach (var record in manifest.export.materials)
                {
                    var where = manifest.path + " " + record.name;
                    var material = ArtModelImportRules.FindProjectMaterial(record.name);
                    Assert.That(material, Is.Not.Null, where);
                    Assert.That(ColorUtility.TryParseHtmlString(record.baseColor, out var approved), Is.True, where);
                    var actual = material.GetColor("_BaseColor");
                    Assert.That(actual.r, Is.EqualTo(approved.r).Within(1.5f / 255f), where);
                    Assert.That(actual.g, Is.EqualTo(approved.g).Within(1.5f / 255f), where);
                    Assert.That(actual.b, Is.EqualTo(approved.b).Within(1.5f / 255f), where);
                    Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(record.metallic).Within(0.01f), where);
                    Assert.That(material.GetFloat("_Smoothness"), Is.EqualTo(record.smoothness).Within(0.01f), where);
                    // A garment is cut at its texture's alpha and drawn from both sides.
                    var garment = manifest.IsGarment || manifest.IsTexture;
                    var alphaClip = material.HasProperty("_AlphaClip") ? material.GetFloat("_AlphaClip") : 0f;
                    Assert.That(alphaClip, Is.EqualTo(garment ? 1f : 0f), where);
                    Assert.That(material.IsKeywordEnabled("_ALPHATEST_ON"), Is.EqualTo(garment), where);
                    if (garment)
                    {
                        Assert.That(material.shader.name, Is.EqualTo(ArtMaterialCreator.GarmentShader), where);
                        Assert.That(material.GetFloat("_Cutoff"), Is.EqualTo(ArtMaterialCreator.GarmentAlphaCutoff), where);
                        Assert.That(material.GetFloat("_Cull"), Is.EqualTo((float)CullMode.Off), where);
                        Assert.That(material.renderQueue, Is.EqualTo((int)RenderQueue.AlphaTest), where);
                    }

                    var baseMap = material.GetTexture("_BaseMap");
                    if (string.IsNullOrEmpty(record.baseMap))
                    {
                        Assert.That(baseMap, Is.Null, where + " has a texture the Blender material does not have");
                        continue;
                    }

                    var texture = manifest.export.textures.SingleOrDefault(entry => entry.name == record.baseMap);
                    Assert.That(texture, Is.Not.Null, where);
                    Assert.That(
                        AssetDatabase.GetAssetPath(baseMap),
                        Is.EqualTo(ArtAssetManifests.ToUnityPath(texture.target)),
                        where);
                }
            }
        }

        [Test]
        public void EveryArtTextureComesFromAnExportAndUsesTheTextureRules()
        {
            var recorded = ExportedManifests()
                .SelectMany(manifest => manifest.export.textures)
                .Select(texture => ArtAssetManifests.ToUnityPath(texture.target))
                .ToArray();
            var paths = AssetDatabase.FindAssets("t:Texture", new[] { ArtModelImportRules.TextureFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToArray();
            Assert.That(paths, Is.EquivalentTo(recorded));
            foreach (var path in paths)
            {
                Assert.That(ArtModelImportRules.AppliesToTexture(path), Is.True, path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.Default), path);
                Assert.That(importer.sRGBTexture, Is.True, path);
                Assert.That(importer.mipmapEnabled, Is.True, path);
                Assert.That(importer.isReadable, Is.False, path);
                Assert.That(importer.maxTextureSize, Is.EqualTo(ArtModelImportRules.MaximumTextureSize), path);
                // Paint layers keep their alpha, where the paint covers the skin, and garments theirs,
                // where they are cut.
                var alpha = ArtModelImportRules.KeepsAlpha(path);
                Assert.That(
                    importer.alphaSource,
                    Is.EqualTo(alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None),
                    path);
                Assert.That(importer.alphaIsTransparency, Is.EqualTo(alpha), path);
            }
        }

        [Test]
        public void EveryArtModelKeepsItsRootsAtTheOriginWithoutCorrections()
        {
            foreach (var path in ModelPaths())
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var manifest = ArtAssetManifests.ForModel(path);
                Assert.That(manifest, Is.Not.Null, path);
                var roots = manifest.RootObjectsOf(path);
                // Blender root objects sit at the origin unrotated, so a baked axis conversion
                // leaves identity transforms; a -90 degree X rotation means the rules did not
                // apply. The meshes of a skinned model are siblings of its armature, and a rigid
                // part such as an eye keeps the position of its pivot.
                foreach (var transform in new[] { model.transform }
                             .Concat(model.transform.Cast<Transform>()))
                {
                    var where = path + " " + transform.name;
                    if (transform == model.transform || roots.Contains(transform.name))
                    {
                        Assert.That(transform.localPosition.magnitude, Is.LessThan(0.0001f), where);
                    }

                    Assert.That(
                        Quaternion.Angle(transform.localRotation, Quaternion.identity),
                        Is.LessThan(0.01f),
                        where);
                    Assert.That((transform.localScale - Vector3.one).magnitude, Is.LessThan(0.0001f), where);
                }

                Assert.That(roots, Is.Not.Empty, path);
                Assert.That(
                    roots.All(root => model.transform.Find(root) != null),
                    Is.True,
                    path + " lacks a root object of its manifest");
                Assert.That(model.GetComponentsInChildren<Collider>(true), Is.Empty, path);
                Assert.That(model.GetComponentsInChildren<Camera>(true), Is.Empty, path);
                Assert.That(model.GetComponentsInChildren<Light>(true), Is.Empty, path);
            }
        }
    }
}
