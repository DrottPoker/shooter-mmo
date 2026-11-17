using System;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEditor.AddressableAssets;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Editor
{
    // Body content for the Editor's tools and the tests, out of a World: a PlayerBodyContent whose
    // loads read the asset database through the Addressables entries and are done at once, so the
    // Wardrobe and a test can show a look of the catalog in the same frame. The game itself loads
    // only through Addressables and never waits; see PlayerBodyContent.
    public static class EditorBodyContent
    {
        // A content set that loads from the asset database.
        public static PlayerBodyContent Create()
        {
            return new PlayerBodyContent(Source.Instance);
        }

        // Asks for the content of the look of the catalog and takes it at once; true when every
        // entry of it is ready or failed.
        public static bool Load(PlayerBodyContent content, BodyAppearanceCatalog catalog, BodyLook look)
        {
            var needs = PlayerBodyContent.NeedsOf(catalog, look);
            content.Want(needs);
            content.Update();
            return content.Want(needs);
        }

        // The content of every garment and attachment of the catalog and every image it names.
        public static bool LoadAll(PlayerBodyContent content, BodyAppearanceCatalog catalog)
        {
            var loaded = Load(content, catalog, BodyLook.Default);
            foreach (var garment in catalog.Garments)
            {
                loaded &= Load(content, catalog, BodyLook.Default.WithGarment(garment.Name, true));
            }

            foreach (var attachment in catalog.Attachments)
            {
                loaded &= Load(content, catalog, BodyLook.Default.WithAttachment(attachment.Name, true));
            }

            foreach (var paint in catalog.Paints)
            {
                loaded &= Load(content, catalog, BodyLook.Default.WithPaint(paint.Name, true));
            }

            foreach (var slot in catalog.Slots)
            {
                for (var index = 1; index < slot.Options.Count; index++)
                {
                    loaded &= Load(content, catalog, BodyLook.Default.With(slot.Name, slot.Options[index].Name));
                }
            }

            return loaded;
        }

        private sealed class Source : IBodyContentSource
        {
            public static readonly Source Instance = new Source();

            public bool TryStart<T>(string address, out IAssetLoad load, out string error)
                where T : Object
            {
                var type = typeof(T);
                load = null;
                error = string.Empty;
                var settings = AddressableAssetSettingsDefaultObject.Settings;
                if (settings != null)
                {
                    foreach (var group in settings.groups)
                    {
                        if (group == null)
                        {
                            continue;
                        }

                        foreach (var entry in group.entries)
                        {
                            if (string.Equals(entry.address, address, StringComparison.Ordinal))
                            {
                                var asset = AssetDatabase.LoadAssetAtPath(entry.AssetPath, type);
                                load = new Done(asset, asset == null ? entry.AssetPath + " is no " + type.Name + "." : string.Empty);
                                return true;
                            }
                        }
                    }
                }

                error = "No Addressables entry has the address '" + address + "'.";
                return false;
            }
        }

        private sealed class Done : IAssetLoad
        {
            public Done(Object result, string error)
            {
                Result = result;
                Error = error;
            }

            public bool IsDone
            {
                get { return true; }
            }

            public Object Result { get; private set; }

            public string Error { get; }

            public void Release()
            {
                Result = null;
            }
        }
    }
}
