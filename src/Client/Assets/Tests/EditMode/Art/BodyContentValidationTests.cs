using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The content of looks loads on demand, so a broken address would be a body that shows without
    // a piece: these checks make it a build defect. Every address of both catalogs resolves through
    // Addressables to content of the right type, nothing that every World loads holds a garment's or
    // an attachment's content, and no bundle holds a copy of another's asset. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Body Content.
    public sealed class BodyContentValidationTests
    {
        private static readonly string[] PlayerPrefabPaths =
        {
            "Assets/Prefabs/Player/LocalPlayer.prefab",
            "Assets/Prefabs/Player/RemotePlayer.prefab",
            "Assets/Prefabs/Player/HumanMaleBody.prefab",
            "Assets/Prefabs/Player/HumanFemaleBody.prefab",
        };

        [Test]
        public void EveryAddressOfBothCatalogsResolvesToContentOfItsType()
        {
            foreach (var path in TestBodyContent.CatalogPaths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                Assert.That(catalog.Garments, Is.Not.Empty, path);
                Assert.That(catalog.Attachments, Is.Not.Empty, path);
                foreach (var garment in catalog.Garments)
                {
                    var where = path + " garment " + garment.Name;
                    WithPiece(garment.Address, where, piece =>
                    {
                        Assert.That(piece.Model, Is.Not.Null, where);
                        Assert.That(piece.Model.GetComponentInChildren<SkinnedMeshRenderer>(true), Is.Not.Null, where);
                        Assert.That(piece.Material, Is.Not.Null, where);
                        Assert.That(piece.Material.name, Is.EqualTo("M_Garment_" + garment.Name), where);
                    });
                }

                foreach (var attachment in catalog.Attachments)
                {
                    var where = path + " attachment " + attachment.Name;
                    WithPiece(attachment.Address, where, piece =>
                    {
                        Assert.That(piece.Model, Is.Not.Null, where);
                        Assert.That(piece.Model.transform.Find(PlayerBodyMeshes.AttachmentRoot), Is.Not.Null, where);
                        Assert.That(piece.Clips.Select(clip => clip.name), Is.EqualTo(attachment.ClipNames), where);
                    });
                }

                var images = catalog.Paints.Select(paint => (paint.Address, "paint " + paint.Name))
                    .Concat(catalog.Slots.SelectMany(slot => slot.Options.Skip(1).Select(option => (option.Address, "option " + slot.Name + " " + option.Name))));
                foreach (var (address, what) in images)
                {
                    var where = path + " " + what;
                    Assert.That(ClientAssets.TryLoadNow<Texture2D>(address, out var handle, out var error), Is.True, where + ": " + error);
                    ClientAssets.Release(handle);
                }
            }
        }

        // The player prefabs and the World scenes, which every World loads, hold no garment's or
        // attachment's model, material, or image, and no image of an option or a paint layer.
        [Test]
        public void NothingEveryWorldLoadsHoldsTheContentOfLooks()
        {
            var looks = ArtAppearanceCatalogs.ManifestsWithLooks().Select(ArtAppearanceCatalogs.LooksFor).ToArray();
            var content = new HashSet<string>();
            foreach (var piece in looks.SelectMany(body => body.Pieces))
            {
                var roots = new List<string> { AssetDatabase.GetAssetPath(piece.Model) };
                if (piece.Material != null)
                {
                    roots.Add(AssetDatabase.GetAssetPath(piece.Material));
                }

                foreach (var dependency in AssetDatabase.GetDependencies(roots.ToArray(), true))
                {
                    var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                    if (type == typeof(GameObject) || type == typeof(Material) || typeof(Texture).IsAssignableFrom(type))
                    {
                        content.Add(dependency);
                    }
                }
            }

            foreach (var texture in looks.SelectMany(body => body.Textures))
            {
                content.Add(AssetDatabase.GetAssetPath(texture.Texture));
            }

            Assert.That(content, Is.Not.Empty);
            var scenes = ClientAddressablesLayout.ResolveCatalogScenes(out var problems).Values;
            Assert.That(problems, Is.Empty);
            foreach (var root in PlayerPrefabPaths.Concat(scenes))
            {
                var held = AssetDatabase.GetDependencies(root, true).Where(content.Contains).ToArray();
                Assert.That(held, Is.Empty, root + " holds content that loads on demand: " + string.Join(", ", held));
            }
        }

        [Test]
        public void NoAssetIsAnImplicitDependencyOfTwoBundles()
        {
            var duplicates = ArtBodyContentEntries.Duplicates();
            Assert.That(duplicates, Is.Empty, string.Join("\n", duplicates.Select(pair => pair.Key + " in " + string.Join(", ", pair.Value))));
        }

        private static void WithPiece(string address, string where, System.Action<BodyPieceContent> check)
        {
            Assert.That(address, Is.Not.Empty, where);
            Assert.That(ClientAssets.TryLoadNow<BodyPieceContent>(address, out var handle, out var error), Is.True, where + ": " + error);
            try
            {
                check(handle.Result);
            }
            finally
            {
                ClientAssets.Release(handle);
            }
        }
    }
}
