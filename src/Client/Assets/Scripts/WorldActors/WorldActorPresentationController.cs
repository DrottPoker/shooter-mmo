using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.WorldActors
{
    // Presents replicated world actors. The registry and each archetype prefab are
    // Addressables: the registry loads when the World scene opens, a prefab loads the first
    // time an actor of its archetype appears, and every handle is released when the World
    // scene closes. An actor whose prefab is still loading is presented once it is ready.
    // Snapshot entities that name a presented Mob move its view; WorldSceneContext moves the
    // remote players of the same snapshots. A snapshot of a Mob whose prefab still loads is
    // lost, so one that showed the Mob away from its pose is noted, and its view starts at its
    // next snapshot instead of sliding from that pose.
    [DisallowMultipleComponent]
    public sealed class WorldActorPresentationController : MonoBehaviour, IRemoteMotionSource
    {
        private readonly Dictionary<ulong, WorldActorView> presentations =
            new Dictionary<ulong, WorldActorView>();
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> prefabLoads =
            new Dictionary<string, AsyncOperationHandle<GameObject>>(StringComparer.Ordinal);
        private readonly HashSet<string> reportedArchetypes =
            new HashSet<string>(StringComparer.Ordinal);
        // Mobs that left while falling dead, so the corpse that replaces each can play on the fall.
        private readonly List<CreatureFall> falls = new List<CreatureFall>();
        // Mobs without a view yet, such as while their prefab loads, whose lost records showed
        // them away from the pose of their actor, by the state revision of that pose.
        private readonly Dictionary<ulong, long> missedMoves = new Dictionary<ulong, long>();
        // The actors a full rebuild found gone, reused so a rebuild allocates nothing.
        private readonly List<ulong> goneIds = new List<ulong>();
        private Action<RealtimeEntitySnapshot> noteUnrouted;
        private MaterialPropertyBlock materialProperties;
        private WorldActorClientController actorController;
        private IRealtimeMovementChannel realtimeMovement;
        private Material primitiveMaterial;
        private bool snapshotsSubscribed;
        private AsyncOperationHandle<WorldActorPresentationRegistry> registryLoad;
        private WorldActorPresentationRegistry presentationRegistry;
        private bool registryResolved;
        private Transform presentationRoot;

        private enum PrefabState
        {
            Ready,
            Loading,
            Fallback
        }

        // A Mob that left the world while its creature fell dead: where and when, and how far into
        // Death it was.
        private readonly struct CreatureFall
        {
            public CreatureFall(string presentationArchetypeId, Vector3 position, float time, float secondsIntoDeath)
            {
                PresentationArchetypeId = presentationArchetypeId;
                Position = position;
                Time = time;
                SecondsIntoDeath = secondsIntoDeath;
            }

            public string PresentationArchetypeId { get; }

            public Vector3 Position { get; }

            public float Time { get; }

            public float SecondsIntoDeath { get; }
        }

        // A corpse takes over the fall of a Mob that left this long ago at most, this near it.
        public const float FallHandoverSeconds = 3f;
        public const float FallHandoverDistance = 1.5f;

        // The views of every presented actor, such as the Mobs whose creatures show their life state.
        // Whether each Mob that snapshots move ran out of them while it moved. Actors that never
        // move, such as NPCs and resource nodes, are left out.
        public void CollectRemoteMotion(List<RemoteMotionSample> samples)
        {
            foreach (var pair in presentations)
            {
                if (pair.Value is MobView mob && mob != null && mob.Movement.IsDriven)
                {
                    samples.Add(new RemoteMotionSample(pair.Key, mob.Movement.IsStarved));
                }
            }
        }

        public Dictionary<ulong, WorldActorView>.ValueCollection Views
        {
            get { return presentations.Values; }
        }

        // The loaded prefab of an archetype for another presentation of it, such as a Mob's corpse:
        // true with the prefab once it is loaded; false while it loads, with loading true, or when
        // the archetype has none. A load it starts presents the actors waiting for it when done.
        // Until the registry itself has loaded every archetype counts as loading, so a corpse that
        // arrives first still gets its model.
        public bool TryGetArchetypePrefab(string presentationArchetypeId, out GameObject prefab, out bool loading)
        {
            if (!registryResolved)
            {
                prefab = null;
                loading = true;
                return false;
            }

            var state = ResolvePrefab(presentationArchetypeId, out prefab);
            loading = state == PrefabState.Loading;
            return state == PrefabState.Ready && prefab != null;
        }

        // Whether a Mob of the archetype still falls dead near the position. The worker sends a
        // Mob's corpse before the Mob leaves, so the corpse waits for it and plays on its fall.
        public bool IsFallingNear(string presentationArchetypeId, Vector3 position)
        {
            foreach (var view in presentations.Values)
            {
                if (view == null
                    || !view.isActiveAndEnabled
                    || !string.Equals(view.PresentationArchetypeId, presentationArchetypeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var creature = view is MobView mob ? mob.Creature : null;
                var offset = view.transform.position - position;
                offset.y = 0f;
                if (creature != null
                    && creature.IsDead
                    && offset.sqrMagnitude <= FallHandoverDistance * FallHandoverDistance)
                {
                    return true;
                }
            }

            return false;
        }

        // How far into Death the corpse of a Mob of the archetype should start, at the position:
        // where a Mob of that archetype just left while falling, the rest of its fall; otherwise
        // false, and the corpse lies in the last pose. Each fall hands over to one corpse.
        public bool TryTakeFall(string presentationArchetypeId, Vector3 position, out float secondsIntoDeath)
        {
            secondsIntoDeath = -1f;
            var now = Time.time;
            falls.RemoveAll(fall => now - fall.Time > FallHandoverSeconds);
            for (var index = 0; index < falls.Count; index++)
            {
                var fall = falls[index];
                var offset = fall.Position - position;
                offset.y = 0f;
                if (string.Equals(fall.PresentationArchetypeId, presentationArchetypeId, StringComparison.Ordinal)
                    && offset.sqrMagnitude <= FallHandoverDistance * FallHandoverDistance)
                {
                    secondsIntoDeath = fall.SecondsIntoDeath + (now - fall.Time);
                    falls.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            WorldActorClientController worldActors,
            IRealtimeMovementChannel movement,
            Material runtimePrimitiveMaterial)
        {
            actorController = worldActors;
            realtimeMovement = movement;
            primitiveMaterial = runtimePrimitiveMaterial;
        }

        // The presentation of an actor by its network entity id, once its prefab is ready.
        public bool TryGetView(ulong entityId, out WorldActorView view)
        {
            return presentations.TryGetValue(entityId, out view) && view != null;
        }

        // Hands every snapshot entity that names a presented Mob to its view. Every other id,
        // such as a player, an NPC, or an entity without a presentation, is ignored. Returns
        // how many entities it handed on.
        public static int RouteSnapshot(
            RealtimeSimulationSnapshot snapshot,
            IReadOnlyDictionary<ulong, WorldActorView> views)
        {
            return RouteSnapshot(snapshot, views, null);
        }

        // As above, and hands every entity that names no presented Mob to unrouted.
        public static int RouteSnapshot(
            RealtimeSimulationSnapshot snapshot,
            IReadOnlyDictionary<ulong, WorldActorView> views,
            Action<RealtimeEntitySnapshot> unrouted)
        {
            if (snapshot == null || snapshot.Entities == null || views == null)
            {
                return 0;
            }

            var routed = 0;
            for (var index = 0; index < snapshot.Entities.Length; index++)
            {
                var entity = snapshot.Entities[index];
                if (entity == null)
                {
                    continue;
                }

                if (!views.TryGetValue(entity.EntityId, out var view)
                    || !(view is MobView mob)
                    || mob == null)
                {
                    unrouted?.Invoke(entity);
                    continue;
                }

                mob.PushSnapshot(snapshot.ServerTick, entity);
                routed++;
            }

            return routed;
        }

        // Whether a record shows a Mob away from the pose of its actor, as one does once the Mob
        // walked off its rest pose; within the record's precision is the same place.
        public static bool ShowsAwayFromPose(WorldActorClientEntry actor, RealtimeEntitySnapshot record)
        {
            return actor != null
                && record != null
                && Vector3.Distance(
                    new Vector3(actor.PositionX, actor.PositionY, actor.PositionZ),
                    new Vector3(record.PositionX, record.PositionY, record.PositionZ))
                    > RemoteActorMovement.SamePositionTolerance;
        }

        // Whether a Mob view created now must start at its next snapshot: a record that showed
        // the Mob away from its resting pose was lost while it had no view, and no newer state
        // came since. A Mob whose spawn walked starts by its own rule.
        public static bool MissedAMove(
            WorldActorClientEntry actor,
            IReadOnlyDictionary<ulong, long> missedMoves)
        {
            return actor != null
                && !actor.IsMoving
                && missedMoves != null
                && missedMoves.TryGetValue(actor.EntityId, out var revision)
                && revision == actor.StateRevision;
        }

        private void Awake()
        {
            materialProperties = new MaterialPropertyBlock();
            noteUnrouted = NoteUnrouted;
        }

        private void Start()
        {
            presentationRoot = new GameObject("WorldActorPresentations").transform;
            presentationRoot.SetParent(transform, false);
            if (ClientAssets.TryLoadAsync(
                    ClientAddresses.WorldActorPresentationRegistry,
                    out registryLoad,
                    out var error))
            {
                if (registryLoad.IsDone)
                {
                    ResolveRegistry();
                }
                else
                {
                    registryLoad.Completed += _ => OnRegistryLoaded();
                }
            }
            else
            {
                ReportMissingRegistry(error);
            }

            if (actorController != null)
            {
                actorController.State.ActorChanged += OnActorChanged;
                actorController.State.Cleared += Rebuild;
                Rebuild();
            }

            if (realtimeMovement != null)
            {
                realtimeMovement.SimulationSnapshotReceived += OnSimulationSnapshotReceived;
                snapshotsSubscribed = true;
            }
        }

        private void OnDestroy()
        {
            if (actorController != null)
            {
                actorController.State.ActorChanged -= OnActorChanged;
                actorController.State.Cleared -= Rebuild;
            }

            if (snapshotsSubscribed)
            {
                realtimeMovement.SimulationSnapshotReceived -= OnSimulationSnapshotReceived;
                snapshotsSubscribed = false;
            }

            foreach (var entityId in presentations.Keys.ToArray())
            {
                DestroyPresentation(entityId);
            }

            foreach (var load in prefabLoads.Values)
            {
                ClientAssets.Release(load);
            }

            prefabLoads.Clear();
            missedMoves.Clear();
            ClientAssets.Release(registryLoad);
            registryLoad = default;
            presentationRegistry = null;
        }

        private void OnSimulationSnapshotReceived(RealtimeSimulationSnapshot snapshot)
        {
            RouteSnapshot(snapshot, presentations, noteUnrouted);
        }

        // Remembers a lost record of a Mob without a view that shows it away from its pose, so
        // the view that appears later does not slide it from there.
        private void NoteUnrouted(RealtimeEntitySnapshot record)
        {
            if (actorController == null
                || presentations.ContainsKey(record.EntityId)
                || !actorController.State.TryGet(record.EntityId, out var actor)
                || actor.Kind != RealtimeWorldActorKind.Mob
                || !actor.IsActive
                || !ShowsAwayFromPose(actor, record))
            {
                return;
            }

            missedMoves[record.EntityId] = actor.StateRevision;
        }

        private void OnRegistryLoaded()
        {
            // The World scene may have closed while the registry was loading.
            if (this == null)
            {
                return;
            }

            ResolveRegistry();
            Rebuild();
        }

        private void ResolveRegistry()
        {
            registryResolved = true;
            if (registryLoad.Status == AsyncOperationStatus.Succeeded && registryLoad.Result != null)
            {
                presentationRegistry = registryLoad.Result;
                return;
            }

            ReportMissingRegistry(registryLoad.OperationException != null
                ? registryLoad.OperationException.Message
                : "The registry asset did not load.");
        }

        // A missing registry is reported once instead of once per archetype.
        private void ReportMissingRegistry(string reason)
        {
            registryResolved = true;
            ClientLog.Warning(
                ClientLogCategory.Client,
                "World actor presentation registry is unavailable at Addressables address '"
                    + ClientAddresses.WorldActorPresentationRegistry
                    + "': " + reason
                    + " Every actor uses the explicit development fallback.");
        }

        // A change of one actor presents only that actor: a spawn creates its view, a newer state
        // applies to it, and a despawn destroys it. Before the registry resolved nothing is
        // presented; the rebuild after it presents every actor.
        private void OnActorChanged(ulong entityId)
        {
            if (actorController == null || !registryResolved)
            {
                return;
            }

            if (actorController.State.TryGet(entityId, out var actor))
            {
                Present(actor);
            }
            else
            {
                Forget(entityId);
            }
        }

        // Presents every actor again, in the order of its entity id, and forgets every actor that
        // left: when the presentation starts, after the registry or a prefab loaded, and when
        // every actor left at once, as when the session ended.
        private void Rebuild()
        {
            if (actorController == null || !registryResolved)
            {
                return;
            }

            var state = actorController.State;
            goneIds.Clear();
            foreach (var entityId in presentations.Keys)
            {
                if (!state.TryGet(entityId, out _))
                {
                    goneIds.Add(entityId);
                }
            }

            foreach (var entityId in missedMoves.Keys)
            {
                if (!state.TryGet(entityId, out _))
                {
                    goneIds.Add(entityId);
                }
            }

            for (var index = 0; index < goneIds.Count; index++)
            {
                Forget(goneIds[index]);
            }

            var actors = state.Actors;
            for (var index = 0; index < actors.Count; index++)
            {
                Present(actors[index]);
            }
        }

        // Creates the view of an actor once its prefab is ready, and applies the actor to it.
        private void Present(WorldActorClientEntry actor)
        {
            if (!presentations.TryGetValue(actor.EntityId, out var view))
            {
                var prefabState = ResolvePrefab(actor.PresentationArchetypeId, out var prefab);
                if (prefabState == PrefabState.Loading)
                {
                    return;
                }

                view = CreatePresentation(actor, prefab);
                presentations.Add(actor.EntityId, view);
            }

            view.Apply(actor);
        }

        // An actor that left: its view goes, and a move it missed while it had none.
        private void Forget(ulong entityId)
        {
            DestroyPresentation(entityId);
            missedMoves.Remove(entityId);
        }

        private PrefabState ResolvePrefab(string presentationArchetypeId, out GameObject prefab)
        {
            prefab = null;
            if (presentationRegistry == null)
            {
                return PrefabState.Fallback;
            }

            if (!prefabLoads.TryGetValue(presentationArchetypeId, out var load))
            {
                if (!presentationRegistry.TryResolve(presentationArchetypeId, out var reference))
                {
                    ReportArchetype(presentationArchetypeId, "is not mapped");
                    return PrefabState.Fallback;
                }

                if (!ClientAssets.TryLoadAsync(reference.AssetGUID, out load, out var error))
                {
                    ReportArchetype(
                        presentationArchetypeId,
                        "has no loadable prefab (" + error + ")");
                    return PrefabState.Fallback;
                }

                prefabLoads.Add(presentationArchetypeId, load);
                if (!load.IsDone)
                {
                    load.Completed += _ => OnPrefabLoaded();
                    return PrefabState.Loading;
                }
            }

            if (!load.IsDone)
            {
                return PrefabState.Loading;
            }

            if (load.Status == AsyncOperationStatus.Succeeded && load.Result != null)
            {
                prefab = load.Result;
                return PrefabState.Ready;
            }

            ReportArchetype(presentationArchetypeId, "prefab failed to load");
            return PrefabState.Fallback;
        }

        private void OnPrefabLoaded()
        {
            if (this != null)
            {
                Rebuild();
            }
        }

        private void ReportArchetype(string presentationArchetypeId, string problem)
        {
            if (reportedArchetypes.Add(presentationArchetypeId))
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "World actor presentation archetype '"
                        + presentationArchetypeId
                        + "' " + problem + ". Using the explicit development fallback.");
            }
        }

        private WorldActorView CreatePresentation(WorldActorClientEntry actor, GameObject prefab)
        {
            GameObject presentation;
            if (prefab != null)
            {
                presentation = Instantiate(prefab, presentationRoot);
            }
            else
            {
                presentation = RuntimePrimitives.Create(
                    actor.Kind switch
                    {
                        RealtimeWorldActorKind.Npc => PrimitiveType.Capsule,
                        RealtimeWorldActorKind.ResourceNode => PrimitiveType.Sphere,
                        _ => PrimitiveType.Cube
                    },
                    primitiveMaterial);
                presentation.transform.SetParent(presentationRoot, false);
                var existingCollider = presentation.GetComponent<Collider>();
                if (existingCollider != null)
                {
                    existingCollider.enabled = false;
                }

                // Only the fallback primitive is tinted; an authored prefab keeps its look.
                ApplyFallbackColor(presentation, actor);
            }

            presentation.name = "WorldActor_" + actor.ActorDefinitionId
                + "_" + actor.EntityId;
            var view = presentation.GetComponent<WorldActorView>();
            if (view == null)
            {
                view = actor.Kind switch
                {
                    RealtimeWorldActorKind.Npc => presentation.AddComponent<NpcView>(),
                    RealtimeWorldActorKind.ResourceNode => presentation.AddComponent<ResourceNodeView>(),
                    _ => presentation.AddComponent<MobView>()
                };
            }

            if (view is MobView mob)
            {
                mob.ConfigureMovement(realtimeMovement != null ? realtimeMovement.MovementSession : null);
            }

            view.Initialize(actor);

            // A Mob that walked off its pose while its prefab loaded is shown where it is next.
            if (view is MobView created && MissedAMove(actor, missedMoves))
            {
                created.StartAtNextSnapshot();
            }

            missedMoves.Remove(actor.EntityId);
            return view;
        }

        private void ApplyFallbackColor(
            GameObject presentation,
            WorldActorClientEntry actor)
        {
            var renderer = presentation.GetComponentInChildren<Renderer>();
            if (renderer == null)
            {
                return;
            }

            var color = actor.Kind switch
            {
                RealtimeWorldActorKind.Npc => new Color(0.20f, 0.55f, 0.85f, 1f),
                RealtimeWorldActorKind.ResourceNode => new Color(0.55f, 0.42f, 0.30f, 1f),
                _ => new Color(0.70f, 0.22f, 0.18f, 1f)
            };
            renderer.GetPropertyBlock(materialProperties);
            materialProperties.SetColor("_Color", color);
            materialProperties.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(materialProperties);
        }

        private void DestroyPresentation(ulong entityId)
        {
            if (!presentations.Remove(entityId, out var view) || view == null)
            {
                return;
            }

            var creature = view is MobView mob ? mob.Creature : null;
            if (creature != null && creature.IsDead)
            {
                falls.Add(new CreatureFall(
                    view.PresentationArchetypeId,
                    view.transform.position,
                    Time.time,
                    creature.SecondsIntoDeath));
            }

            if (Application.isPlaying)
            {
                Destroy(view.gameObject);
            }
            else
            {
                DestroyImmediate(view.gameObject);
            }
        }
    }
}
