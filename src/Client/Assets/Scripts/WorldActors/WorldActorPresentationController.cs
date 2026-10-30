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
    // remote players of the same snapshots.
    [DisallowMultipleComponent]
    public sealed class WorldActorPresentationController : MonoBehaviour
    {
        private readonly Dictionary<ulong, WorldActorView> presentations =
            new Dictionary<ulong, WorldActorView>();
        private readonly Dictionary<string, AsyncOperationHandle<GameObject>> prefabLoads =
            new Dictionary<string, AsyncOperationHandle<GameObject>>(StringComparer.Ordinal);
        private readonly HashSet<string> reportedArchetypes =
            new HashSet<string>(StringComparer.Ordinal);
        // Mobs that left while falling dead, so the corpse that replaces each can play on the fall.
        private readonly List<CreatureFall> falls = new List<CreatureFall>();
        private MaterialPropertyBlock materialProperties;
        private WorldActorClientController actorController;
        private RealtimeSimulationClient simulationClient;
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
        public Dictionary<ulong, WorldActorView>.ValueCollection Views
        {
            get { return presentations.Values; }
        }

        // The loaded prefab of an archetype for another presentation of it, such as a Mob's corpse:
        // true with the prefab once it is loaded; false while it loads, with loading true, or when
        // the archetype has none. A load it starts presents the actors waiting for it when done.
        public bool TryGetArchetypePrefab(string presentationArchetypeId, out GameObject prefab, out bool loading)
        {
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

                var creature = view.GetComponent<CreatureAnimator>();
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
            RealtimeSimulationClient realtimeClient,
            Material runtimePrimitiveMaterial)
        {
            actorController = worldActors;
            simulationClient = realtimeClient;
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
            if (snapshot == null || snapshot.Entities == null || views == null)
            {
                return 0;
            }

            var routed = 0;
            for (var index = 0; index < snapshot.Entities.Length; index++)
            {
                var entity = snapshot.Entities[index];
                if (entity == null
                    || !views.TryGetValue(entity.EntityId, out var view)
                    || !(view is MobView mob)
                    || mob == null)
                {
                    continue;
                }

                mob.PushSnapshot(snapshot.ServerTick, entity.State);
                routed++;
            }

            return routed;
        }

        private void Awake()
        {
            materialProperties = new MaterialPropertyBlock();
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
                actorController.State.Changed += Rebuild;
                Rebuild();
            }

            if (simulationClient != null)
            {
                simulationClient.SimulationSnapshotReceived += OnSimulationSnapshotReceived;
                snapshotsSubscribed = true;
            }
        }

        private void OnDestroy()
        {
            if (actorController != null)
            {
                actorController.State.Changed -= Rebuild;
            }

            if (snapshotsSubscribed)
            {
                simulationClient.SimulationSnapshotReceived -= OnSimulationSnapshotReceived;
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
            ClientAssets.Release(registryLoad);
            registryLoad = default;
            presentationRegistry = null;
        }

        private void OnSimulationSnapshotReceived(RealtimeSimulationSnapshot snapshot)
        {
            RouteSnapshot(snapshot, presentations);
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

        private void Rebuild()
        {
            if (actorController == null || !registryResolved)
            {
                return;
            }

            var currentIds = new HashSet<ulong>(
                actorController.State.Actors.Select(actor => actor.EntityId));
            foreach (var staleId in presentations.Keys
                         .Where(entityId => !currentIds.Contains(entityId))
                         .ToArray())
            {
                DestroyPresentation(staleId);
            }

            foreach (var actor in actorController.State.Actors)
            {
                if (!presentations.TryGetValue(actor.EntityId, out var view))
                {
                    var state = ResolvePrefab(actor.PresentationArchetypeId, out var prefab);
                    if (state == PrefabState.Loading)
                    {
                        continue;
                    }

                    view = CreatePresentation(actor, prefab);
                    presentations.Add(actor.EntityId, view);
                }

                view.Apply(actor);
            }
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
                mob.ConfigureMovement(simulationClient != null ? simulationClient.MovementSession : null);
            }

            view.Initialize(actor);
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

            var creature = view.GetComponent<CreatureAnimator>();
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
