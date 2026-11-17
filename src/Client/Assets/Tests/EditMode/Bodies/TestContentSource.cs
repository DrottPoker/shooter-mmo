using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Tests.EditMode
{
    // Body content for tests of catalogs they make themselves: assets the test gives by address,
    // whose loads are done at once or once the test completes them, that fail when the asset is
    // missing or of another type, and that count their releases. EditorBodyContent.Load takes the
    // content of a look from any set, also from one over this source.
    internal sealed class TestContentSource : IBodyContentSource
    {
        public readonly Dictionary<string, Object> Assets = new Dictionary<string, Object>(StringComparer.Ordinal);
        public readonly List<Load> Loads = new List<Load>();

        // Whether a load is done as it starts.
        public bool Immediate = true;

        public TestContentSource With(string address, Object asset)
        {
            Assets[address] = asset;
            return this;
        }

        public PlayerBodyContent Content()
        {
            return new PlayerBodyContent(this);
        }

        public bool TryStart<T>(string address, out IAssetLoad load, out string error)
            where T : Object
        {
            var type = typeof(T);
            load = null;
            error = string.Empty;
            if (!Assets.TryGetValue(address, out var asset))
            {
                error = "No entry has the address '" + address + "'.";
                return false;
            }

            var started = new Load(address, asset != null && type.IsInstanceOfType(asset) ? asset : null) { IsDone = Immediate };
            Loads.Add(started);
            load = started;
            return true;
        }

        public void CompleteAll()
        {
            foreach (var load in Loads)
            {
                load.IsDone = true;
            }
        }

        internal sealed class Load : IAssetLoad
        {
            private readonly Object asset;

            public Load(string address, Object asset)
            {
                Address = address;
                this.asset = asset;
            }

            public string Address { get; }

            public bool IsDone { get; set; }

            public bool Released { get; private set; }

            public Object Result
            {
                get { return IsDone && !Released ? asset : null; }
            }

            public string Error
            {
                get { return asset == null ? Address + " is no asset of the asked type." : string.Empty; }
            }

            public void Release()
            {
                Released = true;
            }
        }
    }

    // The content of both bodies' catalogs for tests that show their looks, loaded as the Wardrobe
    // loads it, through EditorBodyContent.
    internal static class TestBodyContent
    {
        public static readonly string[] CatalogPaths =
        {
            "Assets/Art/Appearance/Human_Male_Appearance.asset",
            "Assets/Art/Appearance/Human_Female_Appearance.asset",
        };

        // A content set with every garment, attachment, and image of both catalogs loaded.
        public static PlayerBodyContent Loaded()
        {
            var content = EditorBodyContent.Create();
            foreach (var path in CatalogPaths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>(path);
                Assert.That(EditorBodyContent.LoadAll(content, catalog), Is.True, path);
            }

            return content;
        }

        // The content asset at an address, as its entry names it.
        public static BodyPieceContent Piece(string address)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            foreach (var group in settings.groups)
            {
                if (group == null)
                {
                    continue;
                }

                foreach (var entry in group.entries)
                {
                    if (entry.address == address)
                    {
                        return AssetDatabase.LoadAssetAtPath<BodyPieceContent>(entry.AssetPath);
                    }
                }
            }

            Assert.Fail("No Addressables entry has the address " + address + ".");
            return null;
        }

        public static GameObject ModelOf(BodyAppearanceCatalog.Attachment attachment)
        {
            return Piece(attachment.Address).Model;
        }

        public static GameObject ModelOf(BodyAppearanceCatalog.Garment garment)
        {
            return Piece(garment.Address).Model;
        }

        public static Material MaterialOf(BodyAppearanceCatalog.Garment garment)
        {
            return Piece(garment.Address).Material;
        }
    }
}
