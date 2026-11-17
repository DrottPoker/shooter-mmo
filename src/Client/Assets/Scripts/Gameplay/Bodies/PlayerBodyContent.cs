using System;
using System.Collections.Generic;
using Mmorpg.Client.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Mmorpg.Client.Gameplay
{
    // Where the loads of body content come from: Addressables in the game, and the Editor's asset
    // database in the Wardrobe and the tests.
    public interface IBodyContentSource
    {
        // Starts loading the asset of the type at the address; false with the error when no entry
        // has it.
        bool TryStart<T>(string address, out IAssetLoad load, out string error)
            where T : Object;
    }

    // Makes loaded content draw without a first-use hitch, as by drawing its materials once.
    public interface IBodyContentWarmUp
    {
        // Whether the content draws as any other does; while it does not, the warm-up prepares it.
        bool IsWarm(Object content);
    }

    // The content of the looks in view: the garments, attachments, and images of options and paint
    // layers that a BodyAppearanceCatalog names by address, loaded asynchronously, one load and one
    // release per entry, and kept only while a look uses it. One set serves a World visit, beside
    // PlayerBodyMeshes and PlayerBodyMaterials. A look's content is its Needs: a body that waits
    // for a look asks for its needs every frame (Want), and a body, the preview, a corpse, or the
    // warm-up that shows a look counts as a user of every entry of it (AddUser) until it shows
    // another (Release). An entry without users that nobody asked for within WantedFrames goes
    // after GraceSeconds; it raises Releasing first, so the mesh and material caches drop what they
    // made of it before its bundle unloads. A loaded garment with loose cloth has its cloth measured
    // and every new material variant drawn once before it counts as ready, a few entries a frame
    // within the presentation budget. A load that fails logs one error per address and counts as
    // ready, so the body shows without that piece. Nothing here waits for a load. See
    // docs/UNITY_CLIENT_ARCHITECTURE.md, Body Content.
    public sealed class PlayerBodyContent : IDisposable
    {
        public const float GraceSeconds = PlayerBodyMeshes.GraceSeconds;
        public const int WantedFrames = PlayerBodyMeshes.WantedFrames;

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<Entry> loading = new List<Entry>();
        private readonly List<Entry> preparing = new List<Entry>();
        // The entries without users, in the order they lost their last one.
        private readonly List<Entry> unused = new List<Entry>();
        private readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private readonly IBodyContentSource source;
        private bool disposed;

        public PlayerBodyContent()
            : this(null)
        {
        }

        public PlayerBodyContent(IBodyContentSource contentSource)
        {
            source = contentSource ?? AddressablesSource.Instance;
        }

        // Raised before the content of an entry goes, with its address and its asset.
        public event Action<string, Object> Releasing;

        // Prepares the materials of loaded content to draw, or null to take content as it loads.
        public IBodyContentWarmUp WarmUp { get; set; }

        // The loaded entries of each kind, and the loads that run.
        public int GarmentCount
        {
            get { return CountOf(Kind.Garment); }
        }

        public int AttachmentCount
        {
            get { return CountOf(Kind.Attachment); }
        }

        public int Loading
        {
            get { return loading.Count; }
        }

        // The content of a look of the catalog: the garments it draws, the attachments it holds, the
        // images of the paint layers it shows, and the images of the options it names, by address.
        public static Needs NeedsOf(BodyAppearanceCatalog catalog, BodyLook look)
        {
            var items = new List<Item>();
            if (catalog == null)
            {
                return new Needs(items);
            }

            look = look ?? BodyLook.Default;
            var drawn = PlayerBodyMeshes.Drawn(PlayerBodyAppearance.WornBy(catalog, look));
            foreach (var garment in drawn)
            {
                items.Add(new Item(garment.Address, Kind.Garment, garment.Cloth));
            }

            foreach (var attachment in PlayerBodyAppearance.HeldBy(catalog, look))
            {
                items.Add(new Item(attachment.Address, Kind.Attachment, string.Empty));
            }

            var paints = new List<BodyAppearanceCatalog.Paint>();
            catalog.ShownPaints(look, drawn, paints);
            foreach (var paint in paints)
            {
                if (paint.Address.Length > 0)
                {
                    items.Add(new Item(paint.Address, Kind.Texture, string.Empty));
                }
            }

            foreach (var slot in catalog.Slots)
            {
                var option = slot != null ? slot.ChosenIn(look) : null;
                if (option != null && option.Address.Length > 0)
                {
                    items.Add(new Item(option.Address, Kind.Texture, string.Empty));
                }
            }

            return new Needs(items);
        }

        // Asks for the content of the needs, which then stays at least WantedFrames, and starts the
        // loads it lacks; true once every entry is ready or failed. Allocates nothing unless a load
        // starts.
        public bool Want(Needs needs)
        {
            if (needs == null || disposed)
            {
                return true;
            }

            var ready = true;
            var frame = Time.frameCount;
            for (var index = 0; index < needs.Count; index++)
            {
                var entry = EntryOf(needs, index);
                entry.WantedFrame = frame;
                ready &= entry.IsReady;
            }

            return ready;
        }

        // The needs are still asked for, as by a body that waits but was not reached this frame;
        // starts no load. Allocates nothing.
        public void Touch(Needs needs)
        {
            if (needs == null)
            {
                return;
            }

            var frame = Time.frameCount;
            for (var index = 0; index < needs.Count; index++)
            {
                var entry = needs.Entries[index];
                if (entry != null && !entry.Forgotten)
                {
                    entry.WantedFrame = frame;
                }
            }
        }

        // The caller shows the content of the needs until it releases them; an entry with users
        // stays.
        public void AddUser(Needs needs)
        {
            if (needs == null || disposed)
            {
                return;
            }

            for (var index = 0; index < needs.Count; index++)
            {
                var entry = EntryOf(needs, index);
                if (entry.Users == 0)
                {
                    unused.Remove(entry);
                }

                entry.Users++;
                needs.Held[index] = entry;
            }
        }

        // The caller shows the content of the needs no more; an entry without users goes after its
        // grace time.
        public void Release(Needs needs)
        {
            if (needs == null || disposed)
            {
                return;
            }

            for (var index = 0; index < needs.Count; index++)
            {
                var entry = needs.Held[index];
                needs.Held[index] = null;
                if (entry == null || entry.Forgotten || entry.Users == 0)
                {
                    continue;
                }

                entry.Users--;
                if (entry.Users == 0)
                {
                    entry.UnusedSince = Time.realtimeSinceStartup;
                    unused.Add(entry);
                }
            }
        }

        // The garment's or attachment's content once it is ready, or null.
        public BodyPieceContent PieceOf(string address)
        {
            return ResultOf(address) as BodyPieceContent;
        }

        // The image of an option or a paint layer once it is ready, or null.
        public Texture2D TextureOf(string address)
        {
            return ResultOf(address) as Texture2D;
        }

        // Takes the loads that finished, prepares loaded content while the frame's presentation
        // budget lasts and at least one entry a frame, and lets go of the entries past their grace
        // time. Allocates nothing unless an entry loads, fails, or goes.
        public void Update(PresentationBudget budget = null)
        {
            if (disposed)
            {
                return;
            }

            for (var index = loading.Count - 1; index >= 0; index--)
            {
                var entry = loading[index];
                if (!entry.Load.IsDone)
                {
                    continue;
                }

                loading.RemoveAt(index);
                Loaded(entry);
            }

            var prepared = 0;
            for (var index = 0; index < preparing.Count;)
            {
                if (budget != null && prepared > 0 && budget.IsSpent)
                {
                    break;
                }

                var entry = preparing[index];
                if (Prepare(entry))
                {
                    preparing.RemoveAt(index);
                    prepared++;
                }
                else
                {
                    index++;
                }
            }

            Trim(Time.realtimeSinceStartup, Time.frameCount);
        }

        // Lets go at once of every entry that has no user and is not asked for, as when nothing is
        // going to ask again.
        public void ReleaseUnused()
        {
            Trim(float.PositiveInfinity, int.MaxValue);
        }

        // Calls the action with every ready content, such as for a warm-up of what loaded before it.
        public void ForEachReady(Action<Object> action)
        {
            foreach (var entry in entries.Values)
            {
                if (entry.IsReady && entry.Result != null)
                {
                    action(entry.Result);
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var entry in entries.Values)
            {
                Forget(entry);
            }

            entries.Clear();
            loading.Clear();
            preparing.Clear();
            unused.Clear();
        }

        private int CountOf(Kind kind)
        {
            var count = 0;
            foreach (var entry in entries.Values)
            {
                count += entry.Kind == kind && entry.Result != null ? 1 : 0;
            }

            return count;
        }

        private Object ResultOf(string address)
        {
            return !string.IsNullOrEmpty(address) && entries.TryGetValue(address, out var entry) && entry.IsReady
                ? entry.Result
                : null;
        }

        // The entry of an item of the needs, made and its load started when the set has none.
        private Entry EntryOf(Needs needs, int index)
        {
            var entry = needs.Entries[index];
            if (entry != null && !entry.Forgotten)
            {
                return entry;
            }

            var item = needs.Items[index];
            if (!entries.TryGetValue(item.Address, out entry))
            {
                entry = new Entry(item.Address, item.Kind, item.Cloth)
                {
                    WantedFrame = Time.frameCount,
                    UnusedSince = Time.realtimeSinceStartup,
                };
                entries.Add(item.Address, entry);
                unused.Add(entry);
                Start(entry);
            }

            needs.Entries[index] = entry;
            return entry;
        }

        private void Start(Entry entry)
        {
            string error;
            IAssetLoad load;
            try
            {
                var started = entry.Kind == Kind.Texture
                    ? source.TryStart<Texture2D>(entry.Address, out load, out error)
                    : source.TryStart<BodyPieceContent>(entry.Address, out load, out error);
                if (!started)
                {
                    Fail(entry, error);
                    return;
                }
            }
            catch (Exception exception)
            {
                Fail(entry, exception.Message);
                return;
            }

            entry.Load = load;
            loading.Add(entry);
        }

        private void Loaded(Entry entry)
        {
            var result = entry.Load.Result;
            if (result == null)
            {
                var error = entry.Load.Error;
                entry.Load.Release();
                entry.Load = null;
                Fail(entry, error);
                return;
            }

            entry.Result = result;
            preparing.Add(entry);
        }

        // A failed entry counts as ready without content, so a body shows without the piece; its
        // address is reported once.
        private void Fail(Entry entry, string error)
        {
            entry.Failed = true;
            if (reported.Add(entry.Address))
            {
                Debug.LogError("[BODY CONTENT] " + entry.Address + " cannot be loaded; bodies show without it. " + error);
            }
        }

        // Measures a garment's loose cloth and lets the warm-up draw the content's materials; true
        // once the content is ready.
        private bool Prepare(Entry entry)
        {
            if (!entry.Measured)
            {
                entry.Measured = true;
                var kind = PlayerBodyAppearance.ChainsOf(entry.Cloth);
                var piece = entry.Result as BodyPieceContent;
                if (kind.HasValue && piece != null && piece.Model != null)
                {
                    ClothChainSolver.PrepareReaches(piece.Model, kind.Value);
                }
            }

            if (WarmUp != null && !WarmUp.IsWarm(entry.Result))
            {
                return false;
            }

            entry.IsReady = true;
            return true;
        }

        // Lets go of the entries without users past their grace time that nobody asked for within
        // WantedFrames.
        internal void Trim(float now, int frame)
        {
            for (var index = unused.Count - 1; index >= 0; index--)
            {
                var entry = unused[index];
                if (now - entry.UnusedSince >= GraceSeconds && frame - entry.WantedFrame > WantedFrames)
                {
                    unused.RemoveAt(index);
                    entries.Remove(entry.Address);
                    loading.Remove(entry);
                    preparing.Remove(entry);
                    Forget(entry);
                }
            }
        }

        // The caches drop what they made of the content, then its load is released; a load that
        // still runs is released with it, and its result is never taken.
        private void Forget(Entry entry)
        {
            entry.Forgotten = true;
            entry.IsReady = false;
            if (entry.Result != null)
            {
                Releasing?.Invoke(entry.Address, entry.Result);
                // What was measured of its model stays while another loaded garment has that model.
                var piece = entry.Result as BodyPieceContent;
                if (piece != null && piece.Model != null && (disposed || !HasModel(piece.Model, entry)))
                {
                    ClothChainSolver.ForgetReaches(piece.Model);
                }
            }

            entry.Result = null;
            if (entry.Load != null)
            {
                entry.Load.Release();
                entry.Load = null;
            }
        }

        private bool HasModel(GameObject model, Entry except)
        {
            foreach (var other in entries.Values)
            {
                var piece = other != except ? other.Result as BodyPieceContent : null;
                if (piece != null && piece.Model == model)
                {
                    return true;
                }
            }

            return false;
        }

        internal enum Kind
        {
            Garment,
            Attachment,
            Texture,
        }

        internal readonly struct Item
        {
            public Item(string address, Kind kind, string cloth)
            {
                Address = address ?? string.Empty;
                Kind = kind;
                Cloth = cloth ?? string.Empty;
            }

            public string Address { get; }

            public Kind Kind { get; }

            public string Cloth { get; }
        }

        // The content of one look, by address, with the entries it found in a set. A body keeps the
        // needs of the look it shows, which hold its entries until it releases them.
        public sealed class Needs
        {
            internal Needs(List<Item> items)
            {
                // A look names an address once even when two of its pieces share it.
                var distinct = new List<Item>(items.Count);
                foreach (var item in items)
                {
                    if (item.Address.Length > 0 && !distinct.Exists(other => other.Address == item.Address))
                    {
                        distinct.Add(item);
                    }
                }

                Items = distinct.ToArray();
                Entries = new Entry[Items.Length];
                Held = new Entry[Items.Length];
            }

            public int Count
            {
                get { return Items.Length; }
            }

            // The addresses, for tests and diagnostics.
            public IEnumerable<string> Addresses
            {
                get
                {
                    foreach (var item in Items)
                    {
                        yield return item.Address;
                    }
                }
            }

            internal Item[] Items { get; }

            internal Entry[] Entries { get; }

            // The entries this caller counts as a user of.
            internal Entry[] Held { get; }
        }

        internal sealed class Entry
        {
            public Entry(string address, Kind kind, string cloth)
            {
                Address = address;
                Kind = kind;
                Cloth = cloth;
            }

            public string Address { get; }

            public Kind Kind { get; }

            public string Cloth { get; }

            public IAssetLoad Load { get; set; }

            public Object Result { get; set; }

            public bool Measured { get; set; }

            // Loaded and prepared, or failed.
            public bool IsReady
            {
                get { return ready || Failed; }
                set { ready = value; }
            }

            public bool Failed { get; set; }

            public bool Forgotten { get; set; }

            public int Users { get; set; }

            public int WantedFrame { get; set; }

            public float UnusedSince { get; set; }

            private bool ready;
        }

        private sealed class AddressablesSource : IBodyContentSource
        {
            public static readonly AddressablesSource Instance = new AddressablesSource();

            public bool TryStart<T>(string address, out IAssetLoad load, out string error)
                where T : Object
            {
                return ClientAssets.TryStartLoad<T>(address, out load, out error);
            }
        }
    }
}
