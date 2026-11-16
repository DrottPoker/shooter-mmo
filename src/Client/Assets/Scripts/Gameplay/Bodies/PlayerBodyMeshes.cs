using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;

namespace Mmorpg.Client.Gameplay
{
    // Combined meshes of player bodies. A body is made of skinned parts, one per body zone plus
    // the eyes, teeth, and tongue, and wears garments, meshes skinned to the same skeleton. Drawn
    // as parts, a crowd paid for every part of every body; combined, a body is one skinned
    // renderer with one submesh per material. Putting a body together with garments leaves out
    // the body faces the worn garments cover, moves the pulled-in body vertices inward, leaves out
    // the garment faces whose underlay a garment of a higher layer covers, leaves out every
    // garment of a slot that a worn garment of a higher layer hides, and leaves out the hair
    // under headwear that hides it or draws it in its form under headwear that presses it flat;
    // see docs/EQUIPMENT_VISUALS_DESIGN.md. Attachments, such as a sword, are rigid meshes weighted
    // fully to their socket bone, and a skinned attachment, such as a bow, keeps its own bones, which
    // the body holds below the socket, so a body holding them is still one renderer. Every body with
    // the same visible parts of the same model, the same garments, and the same attachments shares
    // one mesh.
    //
    // A build reads its renderers and models on the main thread (Gather), joins their vertices and
    // triangles (Compute), and makes the mesh (Apply). For and Acquire build at once; Want asks for
    // a build that Update runs on a worker thread, so a body that needs a combination no body has
    // shown waits for it instead of building it in a frame, and both ways make the same mesh. The
    // bodies that show a mesh count as its users; a mesh nobody uses stays GraceSeconds, at most
    // UnusedCap of them, and is then destroyed, so memory comes back when a crowd leaves. The owner
    // destroys the rest.
    public sealed partial class PlayerBodyMeshes : IDisposable
    {
        private static readonly IReadOnlyList<BodyAppearanceCatalog.Garment> NoGarments =
            new BodyAppearanceCatalog.Garment[0];

        private static readonly IReadOnlyList<BodyAppearanceCatalog.Attachment> NoAttachments =
            new BodyAppearanceCatalog.Attachment[0];

        private static readonly ProfilerMarker BuildMarker = new ProfilerMarker("PlayerBodyMeshes.Build");
        private static readonly ProfilerMarker GatherMarker = new ProfilerMarker("PlayerBodyMeshes.Gather");
        private static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("PlayerBodyMeshes.Apply");

        // How far a hand swings from where it is in the bind pose, in meters, which the bounds of a
        // body leave room for around what it holds.
        public const float AttachmentReach = 0.35f;

        // The root object of an attachment's model, where it is held, and a skinned one's armature.
        public const string AttachmentRoot = "Grip";

        // A mesh no body uses any more stays this long, so a player who changes back and forth, or
        // a crowd that leaves and comes back within seconds, builds nothing again; and at most this
        // many such meshes stay, about 0.7 MB each, so a crowd that leaves frees its meshes at once.
        public const float GraceSeconds = 4f;
        public const int UnusedCap = 24;

        // Builds that run on worker threads at the same time.
        public const int ConcurrentBuilds = 3;

        // A build that no body asked for again within this many frames is not started, and a built
        // mesh that no body took is kept as long as one asks for it.
        public const int WantedFrames = 30;

        private readonly Dictionary<string, Combined> meshes = new Dictionary<string, Combined>(StringComparer.Ordinal);
        private readonly List<Combined> queued = new List<Combined>();
        private readonly List<Combined> running = new List<Combined>();
        // The built meshes without a user, in the order they lost their last one.
        private readonly List<Combined> unused = new List<Combined>();
        private readonly Dictionary<PlayerBodyAppearance, (SkinnedMeshRenderer[] Parts, string Key)> bodyParts =
            new Dictionary<PlayerBodyAppearance, (SkinnedMeshRenderer[] Parts, string Key)>();
        // What the builds read of the source meshes, once per set, and the buffers they join
        // meshes in, kept while builds come and let go a grace time after the last one.
        private readonly Dictionary<Mesh, SourceMesh> sources = new Dictionary<Mesh, SourceMesh>();
        private readonly Stack<Buffers> spareBuffers = new Stack<Buffers>();
        private readonly bool releaseCpuCopies;
        private float lastBuildTime;
        private bool disposed;

        public PlayerBodyMeshes()
            : this(false)
        {
        }

        // With releaseCpuCopies a mesh keeps no copy of its data once it is uploaded, as in play,
        // where nothing reads a shared mesh back; out of play, as in the Wardrobe and the tests,
        // the meshes stay readable.
        public PlayerBodyMeshes(bool releaseCpuCopies)
        {
            this.releaseCpuCopies = releaseCpuCopies;
        }

        // How many combined meshes exist, used or kept for their grace time.
        public int Count
        {
            get
            {
                var count = 0;
                foreach (var combined in meshes.Values)
                {
                    count += combined.Mesh != null ? 1 : 0;
                }

                return count;
            }
        }

        // How many builds wait or run.
        public int Pending
        {
            get { return queued.Count + running.Count; }
        }

        // The combined mesh of these parts, built once and then shared. The parts must be
        // bound to one skeleton below the root, with readable meshes.
        public Combined For(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            return For(parts, root, null, NoGarments);
        }

        // Also wearing the garments of the catalog, whose models are skinned to bones of the same
        // names as the parts' skeleton, and holding its attachments on their sockets below the root.
        // Built now when no body has it, also when its build waits or runs.
        public Combined For(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            var combined = Entry(parts, root, catalog, garments, attachments, false);
            Finish(combined);
            return combined;
        }

        // As For, and the caller uses the mesh until it releases it.
        public Combined Acquire(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            var combined = For(parts, root, catalog, garments, attachments);
            AddUser(combined);
            return combined;
        }

        // The caller uses the mesh no more; a mesh without users goes after its grace time.
        public void Release(Combined combined)
        {
            if (disposed || combined == null || combined.Key == null || combined.Users == 0
                || !meshes.TryGetValue(combined.Key, out var known) || known != combined)
            {
                return;
            }

            combined.Users--;
            if (combined.Users == 0)
            {
                combined.UnusedSince = Time.realtimeSinceStartup;
                unused.Add(combined);
            }
        }

        // The combination the body, an instance or its prefab, shows in the look; when it is not
        // built, Update builds it on a worker thread while bodies keep asking for it.
        public Combined Want(PlayerBodyAppearance body, BodyLook look)
        {
            var catalog = body.Catalog;
            var parts = CachedPartsOf(body, out var partsKey);
            var combined = Entry(
                parts,
                body.transform,
                catalog,
                PlayerBodyAppearance.WornBy(catalog, look),
                PlayerBodyAppearance.HeldBy(catalog, look),
                true,
                partsKey);
            Touch(combined);
            return combined;
        }

        // A body still waits for the combination.
        public void Touch(Combined combined)
        {
            if (combined != null)
            {
                combined.WantedFrame = Time.frameCount;
            }
        }

        // Makes the meshes of the builds that finished, starts the waiting builds for which a
        // worker thread is free while the watch is under the budget, and destroys the meshes past
        // their grace time. Allocates nothing unless a build starts or ends.
        public void Update(System.Diagnostics.Stopwatch watch, double budgetMilliseconds)
        {
            for (var index = running.Count - 1; index >= 0; index--)
            {
                var combined = running[index];
                if (!combined.Work.IsCompleted)
                {
                    continue;
                }

                running.RemoveAt(index);
                Complete(combined);
            }

            var frame = Time.frameCount;
            while (running.Count < ConcurrentBuilds && queued.Count > 0
                && (watch == null || watch.Elapsed.TotalMilliseconds < budgetMilliseconds))
            {
                var next = queued[0];
                queued.RemoveAt(0);
                if (next.Users == 0 && frame - next.WantedFrame > WantedFrames)
                {
                    // Nobody waits for it any more, such as a player who left before it began.
                    meshes.Remove(next.Key);
                    next.Forgotten = true;
                    continue;
                }

                Start(next);
            }

            Trim(Time.realtimeSinceStartup, frame);

            // The buffers of the builds go a grace time after the last build, as when a crowd came.
            if (spareBuffers.Count > 0 && queued.Count == 0 && running.Count == 0
                && Time.realtimeSinceStartup - lastBuildTime >= GraceSeconds)
            {
                spareBuffers.Clear();
            }
        }

        // Destroys every mesh without a user at once, as when nothing is going to ask again.
        public void ReleaseUnused()
        {
            Trim(float.PositiveInfinity, int.MaxValue);
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var combined in running)
            {
                try
                {
                    combined.Work.Wait();
                }
                catch (AggregateException)
                {
                    // A failed build has no mesh to destroy.
                }
            }

            foreach (var combined in meshes.Values)
            {
                DestroyMesh(combined);
            }

            meshes.Clear();
            queued.Clear();
            running.Clear();
            unused.Clear();
            bodyParts.Clear();
            sources.Clear();
            spareBuffers.Clear();
        }

        // The skinned parts a body shows combined: its renderers with a mesh that are enabled and
        // active below it, in their order, whether it is an instance or its prefab.
        public static SkinnedMeshRenderer[] PartsOf(Component body)
        {
            var found = new List<SkinnedMeshRenderer>();
            foreach (var renderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.enabled && renderer.sharedMesh != null
                    && renderer.name != PlayerBodyAppearance.CombinedRendererName
                    && IsActiveBelow(renderer.transform, body.transform))
                {
                    found.Add(renderer);
                }
            }

            return found.ToArray();
        }

        private static bool IsActiveBelow(Transform transform, Transform root)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf)
                {
                    return false;
                }

                if (current == root)
                {
                    return true;
                }
            }

            return true;
        }

        // The parts of a body prefab and the part of a key that names them, found once.
        private SkinnedMeshRenderer[] CachedPartsOf(PlayerBodyAppearance body, out string partsKey)
        {
            if (!bodyParts.TryGetValue(body, out var found))
            {
                var parts = PartsOf(body);
                found = (parts, PartsKeyOf(parts));
                bodyParts[body] = found;
            }

            partsKey = found.Key;
            return found.Parts;
        }

        private Combined Entry(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments,
            bool queue, string partsKey = null)
        {
            garments = garments ?? NoGarments;
            attachments = attachments ?? NoAttachments;
            var key = KeyOf(partsKey ?? PartsKeyOf(parts), garments, attachments);
            if (meshes.TryGetValue(key, out var combined))
            {
                return combined;
            }

            combined = new Combined(key)
            {
                Parts = ToArray(parts),
                Root = root,
                Catalog = catalog,
                Garments = ToArray(garments),
                Attachments = ToArray(attachments),
                WantedFrame = Time.frameCount,
            };
            meshes[key] = combined;
            if (queue)
            {
                queued.Add(combined);
            }

            return combined;
        }

        private static T[] ToArray<T>(IReadOnlyList<T> list)
        {
            var array = new T[list.Count];
            for (var index = 0; index < array.Length; index++)
            {
                array[index] = list[index];
            }

            return array;
        }

        // Builds the combination now: a waiting build runs here, and a running one is waited for.
        private void Finish(Combined combined)
        {
            if (combined.Mesh != null)
            {
                return;
            }

            if (combined.Work != null)
            {
                running.Remove(combined);
                try
                {
                    combined.Work.Wait();
                }
                catch (AggregateException)
                {
                    // Complete builds it here again and shows the error.
                }

                Complete(combined);
                if (combined.Mesh != null)
                {
                    return;
                }
            }

            queued.Remove(combined);
            using (BuildMarker.Auto())
            {
                var plan = Plan.Gather(combined.Parts, combined.Root, combined.Catalog, combined.Garments, combined.Attachments, sources);
                var buffers = TakeBuffers();
                plan.Compute(buffers);
                combined.Take(plan.Apply(releaseCpuCopies));
                spareBuffers.Push(buffers);
            }

            Built(combined);
        }

        private void Start(Combined combined)
        {
            Plan plan;
            try
            {
                using (GatherMarker.Auto())
                {
                    plan = Plan.Gather(combined.Parts, combined.Root, combined.Catalog, combined.Garments, combined.Attachments, sources);
                }
            }
            catch (Exception exception)
            {
                // A body that shows it builds it at once and shows the error, as without a build.
                Debug.LogException(exception);
                combined.Failed = true;
                return;
            }

            var buffers = TakeBuffers();
            combined.Plan = plan;
            combined.Work = Task.Run(() => plan.Compute(buffers));
            running.Add(combined);
        }

        private Buffers TakeBuffers()
        {
            lastBuildTime = Time.realtimeSinceStartup;
            return spareBuffers.Count > 0 ? spareBuffers.Pop() : new Buffers();
        }

        private void Complete(Combined combined)
        {
            var plan = (Plan)combined.Plan;
            var work = combined.Work;
            var buffers = plan.Buffers;
            combined.Plan = null;
            combined.Work = null;
            if (work.Status != TaskStatus.RanToCompletion)
            {
                if (work.Exception != null)
                {
                    Debug.LogException(work.Exception.GetBaseException());
                }

                combined.Failed = true;
                spareBuffers.Push(buffers ?? new Buffers());
                return;
            }

            using (ApplyMarker.Auto())
            {
                combined.Take(plan.Apply(releaseCpuCopies));
            }

            spareBuffers.Push(buffers);
            Built(combined);
        }

        // A mesh starts without users; the bodies that asked for it take it in a frame or two.
        private void Built(Combined combined)
        {
            combined.Failed = false;
            combined.Parts = null;
            combined.Root = null;
            combined.Catalog = null;
            combined.Garments = null;
            combined.Attachments = null;
            if (combined.Users == 0)
            {
                combined.UnusedSince = Time.realtimeSinceStartup;
                unused.Add(combined);
            }
        }

        private void AddUser(Combined combined)
        {
            if (combined.Users == 0)
            {
                unused.Remove(combined);
            }

            combined.Users++;
        }

        // Destroys the meshes without users past their grace time, and the oldest beyond the cap;
        // a mesh that a body still asks for stays.
        internal void Trim(float now, int frame)
        {
            for (var index = unused.Count - 1; index >= 0; index--)
            {
                var combined = unused[index];
                if (now - combined.UnusedSince >= GraceSeconds && frame - combined.WantedFrame > WantedFrames)
                {
                    Forget(combined, index);
                }
            }

            for (var index = 0; index < unused.Count && unused.Count > UnusedCap;)
            {
                var combined = unused[index];
                if (frame - combined.WantedFrame > WantedFrames)
                {
                    Forget(combined, index);
                }
                else
                {
                    index++;
                }
            }
        }

        private void Forget(Combined combined, int unusedIndex)
        {
            unused.RemoveAt(unusedIndex);
            meshes.Remove(combined.Key);
            combined.Forgotten = true;
            DestroyMesh(combined);
        }

        private static void DestroyMesh(Combined combined)
        {
            if (combined.Mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(combined.Mesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(combined.Mesh);
            }
        }

        public static Combined Build(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root)
        {
            return Build(parts, root, null, NoGarments);
        }

        // The garments a body draws: the worn ones from the lowest layer up, without those of a slot
        // that a worn garment of a higher layer hides, such as trousers under a floor-length robe;
        // leg armor over the robe stays. Headwear that hides the hair leaves the hair out.
        public static List<BodyAppearanceCatalog.Garment> Drawn(IReadOnlyList<BodyAppearanceCatalog.Garment> garments)
        {
            var drawn = new List<BodyAppearanceCatalog.Garment>();
            foreach (var garment in garments)
            {
                if (garment == null || garment.Model == null)
                {
                    continue;
                }

                var hidden = false;
                foreach (var other in garments)
                {
                    if (other != null && other != garment
                        && ((other.Layer > garment.Layer && Contains(other.Hides, garment.Slot))
                            || (IsHair(garment) && string.Equals(other.Hair, BodyAppearanceCatalog.Garment.HideHair, StringComparison.Ordinal))))
                    {
                        hidden = true;
                        break;
                    }
                }

                if (!hidden)
                {
                    drawn.Add(garment);
                }
            }

            drawn.Sort((a, b) => a.Layer != b.Layer
                ? a.Layer.CompareTo(b.Layer)
                : string.CompareOrdinal(a.Name, b.Name));
            return drawn;
        }

        // The blend shape a drawn garment takes in full, or null: hair under headwear that presses
        // it flat takes its form under headwear.
        public static string ShapeOf(BodyAppearanceCatalog.Garment garment, IReadOnlyList<BodyAppearanceCatalog.Garment> drawn)
        {
            if (!IsHair(garment))
            {
                return null;
            }

            foreach (var other in drawn)
            {
                if (other != null && string.Equals(other.Hair, BodyAppearanceCatalog.Garment.CompressHair, StringComparison.Ordinal))
                {
                    return BodyAppearanceCatalog.Garment.UnderHeadwearShape;
                }
            }

            return null;
        }

        private static bool IsHair(BodyAppearanceCatalog.Garment garment)
        {
            return string.Equals(garment.Slot, BodyAppearanceCatalog.Garment.HairSlot, StringComparison.Ordinal);
        }

        // Every part's and every drawn garment's mesh in the root's space, the bones of all of them
        // in one list by name, and the triangles of each material in one submesh, in the order the
        // materials first appear; vertices no triangle uses are left out. Each attachment's meshes
        // lie on its socket bone as the socket stands on its parent bone in the bind pose, with its
        // root object Grip on the socket; a skinned attachment's meshes lie on its own bones, which
        // stand below the socket as they stand below the model's root. The mesh stays readable.
        public static Combined Build(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root, BodyAppearanceCatalog catalog,
            IReadOnlyList<BodyAppearanceCatalog.Garment> garments, IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            var plan = Plan.Gather(parts, root, catalog, garments ?? NoGarments, attachments ?? NoAttachments,
                new Dictionary<Mesh, SourceMesh>());
            plan.Compute(new Buffers());
            return plan.Apply(false);
        }

        // Builds the combination as Want and Update do, the vertices and triangles on a worker
        // thread, and waits for it; the tests compare it with Build.
        internal static Combined BuildOnWorkerThread(IReadOnlyList<SkinnedMeshRenderer> parts, Transform root,
            BodyAppearanceCatalog catalog, IReadOnlyList<BodyAppearanceCatalog.Garment> garments,
            IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments = null)
        {
            var plan = Plan.Gather(parts, root, catalog, garments ?? NoGarments, attachments ?? NoAttachments,
                new Dictionary<Mesh, SourceMesh>());
            var buffers = new Buffers();
            Task.Run(() => plan.Compute(buffers)).Wait();
            return plan.Apply(false);
        }

        // The bones of a skinned attachment's model, such as a bow's limbs and string, for a body to
        // hold: a copy of its armature, the root object Grip with every transform below it, by name
        // and as they stand, below a new transform of the attachment's name on the socket, which
        // stands where the model's root does.
        public static Transform HoldBones(BodyAppearanceCatalog.Attachment attachment, Transform socket)
        {
            var root = new GameObject(attachment.Name).transform;
            root.SetParent(socket, false);
            var armature = attachment.Model.transform.Find(AttachmentRoot);
            if (armature != null)
            {
                var copy = new GameObject(armature.name).transform;
                copy.SetParent(root, false);
                copy.localPosition = armature.localPosition;
                copy.localRotation = armature.localRotation;
                copy.localScale = armature.localScale;
                CopyChildren(armature, copy);
            }

            return root;
        }

        private static void CopyChildren(Transform from, Transform to)
        {
            foreach (Transform child in from)
            {
                var copy = new GameObject(child.name).transform;
                copy.SetParent(to, false);
                copy.localPosition = child.localPosition;
                copy.localRotation = child.localRotation;
                copy.localScale = child.localScale;
                CopyChildren(child, copy);
            }
        }

        // The attachments with a model, one per socket bone, by name.
        public static List<BodyAppearanceCatalog.Attachment> Held(IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments)
        {
            var held = new List<BodyAppearanceCatalog.Attachment>();
            foreach (var attachment in attachments)
            {
                if (attachment != null && attachment.Model != null
                    && !held.Exists(other => string.Equals(other.Bone, attachment.Bone, StringComparison.Ordinal)))
                {
                    held.Add(attachment);
                }
            }

            held.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return held;
        }

        private static bool Contains(IReadOnlyList<string> names, string name)
        {
            foreach (var candidate in names)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static Transform FindDescendant(Transform parent, string name)
        {
            foreach (var child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        // Bounds in the space of one transform, as bounds in the space of another.
        private static Bounds BoundsIn(Transform from, Bounds local, Transform to)
        {
            if (from == to)
            {
                return local;
            }

            var matrix = to.worldToLocalMatrix * from.localToWorldMatrix;
            var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
            for (var corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -local.extents.x : local.extents.x,
                    (corner & 2) == 0 ? -local.extents.y : local.extents.y,
                    (corner & 4) == 0 ? -local.extents.z : local.extents.z);
                result.Encapsulate(matrix.MultiplyPoint3x4(local.center + offset));
            }

            return result;
        }

        // The part of a key that names the parts: their meshes and materials.
        private static string PartsKeyOf(IReadOnlyList<SkinnedMeshRenderer> parts)
        {
            var key = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                key.Append(part.sharedMesh != null ? part.sharedMesh.GetEntityId().ToString() : "-").Append('/');
                foreach (var material in part.sharedMaterials)
                {
                    key.Append(material != null ? material.GetEntityId().ToString() : "-").Append(',');
                }

                key.Append(';');
            }

            return key.ToString();
        }

        private static string KeyOf(string partsKey, IReadOnlyList<BodyAppearanceCatalog.Garment> garments,
            IReadOnlyList<BodyAppearanceCatalog.Attachment> attachments)
        {
            var key = new System.Text.StringBuilder(partsKey);
            var drawn = Drawn(garments);
            foreach (var garment in drawn)
            {
                key.Append('|').Append(garment.Name).Append('@').Append(garment.Model.GetEntityId().ToString())
                    .Append('/').Append(garment.Material != null ? garment.Material.GetEntityId().ToString() : "-")
                    .Append('~').Append(ShapeOf(garment, drawn) ?? string.Empty);
            }

            foreach (var attachment in Held(attachments))
            {
                key.Append('#').Append(attachment.Name).Append('@').Append(attachment.Model.GetEntityId().ToString())
                    .Append('/').Append(attachment.Bone);
            }

            return key.ToString();
        }

        // A combination of parts, garments, and attachments, and its shared mesh once built.
        public sealed class Combined
        {
            public Combined(Mesh mesh, string[] boneNames, Material[] materials, string rootBoneName, Bounds localBounds)
            {
                Mesh = mesh;
                BoneNames = boneNames;
                Materials = materials;
                RootBoneName = rootBoneName;
                LocalBounds = localBounds;
            }

            internal Combined(string key)
            {
                Key = key;
            }

            public Mesh Mesh { get; private set; }

            // The bones of the mesh by name, so every body binds the shared mesh to its own bones.
            public IReadOnlyList<string> BoneNames { get; private set; }

            public IReadOnlyList<Material> Materials { get; private set; }

            public string RootBoneName { get; private set; }

            public Bounds LocalBounds { get; private set; }

            // Whether a body can show it at once: its mesh is built, or its build failed, which the
            // body then shows as it builds it again.
            public bool IsReady
            {
                get { return Mesh != null || Failed; }
            }

            // The bodies, previews, and bakes that show it now.
            public int Users { get; internal set; }

            internal string Key { get; }

            internal bool Failed { get; set; }

            // The set let go of it, unbuilt or destroyed; whoever still waits for it asks again.
            public bool Forgotten { get; internal set; }

            internal int WantedFrame { get; set; }

            internal float UnusedSince { get; set; }

            // What it is built from, until it is built.
            internal SkinnedMeshRenderer[] Parts { get; set; }

            internal Transform Root { get; set; }

            internal BodyAppearanceCatalog Catalog { get; set; }

            internal BodyAppearanceCatalog.Garment[] Garments { get; set; }

            internal BodyAppearanceCatalog.Attachment[] Attachments { get; set; }

            // The build that runs, and its plan.
            internal Task Work { get; set; }

            internal object Plan { get; set; }

            internal void Take(Combined built)
            {
                Mesh = built.Mesh;
                BoneNames = built.BoneNames;
                Materials = built.Materials;
                RootBoneName = built.RootBoneName;
                LocalBounds = built.LocalBounds;
            }
        }
    }
}
