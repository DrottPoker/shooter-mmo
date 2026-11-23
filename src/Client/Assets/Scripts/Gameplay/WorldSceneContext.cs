using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSceneContext : MonoBehaviour, IRemoteMotionSource
    {
        private static readonly Unity.Profiling.ProfilerMarker PresentBodyMarker =
            new Unity.Profiling.ProfilerMarker("WorldSceneContext.PresentBody");
        private static readonly Unity.Profiling.ProfilerMarker FirstViewMarker =
            new Unity.Profiling.ProfilerMarker("WorldSceneContext.PrepareFirstView");

        [SerializeField] private LocalPlayerController localPlayerPrefab;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;

        private readonly Dictionary<ulong, RemotePlayerView> remotePlayers =
            new Dictionary<ulong, RemotePlayerView>();
        // The content of the looks in view, which the shared meshes and materials draw with, one set
        // per World visit; see EnsureBodyCaches.
        private PlayerBodyContent bodyContent;
        private PlayerBodyMaterials bodyMaterials;
        // Nothing reads the shared meshes back, so they keep no copy once uploaded; the corpse
        // bakes read theirs back and take readable meshes of their own.
        private PlayerBodyMeshes bodyMeshes;
        private PlayerBodyMeshes corpseMeshes;
        private readonly PlayerCorpseBodies corpseBodies = new PlayerCorpseBodies();
        // Looks that wait for a dead body to live again: a dying body keeps its clothes until its
        // character respawns, though its items already lie in its corpse.
        private readonly Dictionary<ulong, RealtimeCharacterAppearance> deferredAppearances =
            new Dictionary<ulong, RealtimeCharacterAppearance>();
        private readonly List<ulong> revivedEntities = new List<ulong>();
        // Players in view whose body waits to be put on, with the look to show.
        private readonly WaitingBodies waitingBodies = new WaitingBodies();
        // The frame's time for putting things into view, which the bodies share with the world
        // actors and the nameplates: making the meshes of the finished builds, starting builds, and
        // putting bodies on players that came into view, the nearest first and at least one a frame
        // whose combination is built, so a crowd that comes into view at once, as at a join, takes
        // a few frames instead of stalling one: a body takes about 2 ms.
        private PresentationBudget presentationBudget = new PresentationBudget();
        private bool localLookDeferred;
        // The local body's look, or the preview's, waits for its content and then for the build of
        // its combination.
        private bool localLookWaiting;
        private bool previewWaiting;
        private PlayerBodyContent.Needs localWantedContent;
        private PlayerBodyContent.Needs previewWantedContent;
        private PlayerBodyMeshes.Combined localWanted;
        private PlayerBodyMeshes.Combined previewWanted;
        private PlayerBodyWarmUp warmUp;
        // Entering the World waits under a cover that hides the World: first for the content of the
        // local player's look and of the default looks, then for the first view, the warm-up's draw,
        // the content of the looks of the players in view, and what the readiness parts wait for,
        // such as the prefabs of the world actors in view, at most until FirstViewCapSeconds.
        private EntryStage entry = EntryStage.Shown;
        private List<PlayerBodyContent.Needs> entryContent;
        private PlayerBodyContent.Needs localEntryContent;
        private PlayerBodyContent.Needs previewEntryContent;
        private readonly List<IWorldEntryReadiness> entryReadiness = new List<IWorldEntryReadiness>();
        private GameObject entryCover;
        private System.Diagnostics.Stopwatch entryWatch;
        private double entryLookMilliseconds;
        private float firstViewCapSeconds = FirstViewCapSeconds;

        // How long after the start of the entry the World shows at the latest once the content of
        // the local look is loaded, also when the rest of the first view is not ready; that rest then
        // comes into view within the presentation budget.
        public const float FirstViewCapSeconds = 1f;

        private enum EntryStage
        {
            Shown,
            LoadingLook,
            PreparingView,
        }
        private IClientSession clientSession;
        private IRealtimeSession realtimeSession;
        private IRealtimeMovementChannel realtimeMovement;
        private IRealtimePlayerChannel realtimePlayers;
        private IRealtimeLookChannel realtimeLooks;
        private IRealtimeAdminChannel realtimeAdmin;
        private LocalPlayerSource localPlayers;
        private WorldInputFocus inputFocus;
        private PlayerBodyTintSetting bodyTint;
        private PlayerBodyLookSetting bodyLook;
        private PlayerEquipment equipment;
        private LocalPlayerController localPlayer;
        private Camera playerCamera;
        private CharacterPreview preview;

        // The local player of the joined session, or null before it spawned.
        public LocalPlayerController LocalPlayer
        {
            get { return localPlayer; }
        }

        // The copy of the local character that the Equipment window shows, dressed as the
        // character is but holding no tool; it exists from Initialize on and shows a body once
        // the local player spawned.
        public CharacterPreview Preview
        {
            get { return preview; }
        }

        // Whether each other player in view ran out of snapshots while it moved.
        public void CollectRemoteMotion(List<RemoteMotionSample> samples)
        {
            foreach (var pair in remotePlayers)
            {
                if (pair.Value != null)
                {
                    samples.Add(new RemoteMotionSample(pair.Key, pair.Value.IsMovementStarved));
                }
            }
        }

        // The presentations of the other players in view.
        public Dictionary<ulong, RemotePlayerView>.ValueCollection RemotePlayers
        {
            get { return remotePlayers.Values; }
        }

        // The combined meshes and the material copies that the shared body caches hold, and the
        // garment and attachment entries the body content holds loaded, which the crowd
        // measurement reads after a crowd left; nothing reads them a frame.
        internal int BodyMeshCount
        {
            get { return bodyMeshes != null ? bodyMeshes.Count + corpseMeshes.Count : 0; }
        }

        internal int BodyMaterialCount
        {
            get { return bodyMaterials != null ? bodyMaterials.Count : 0; }
        }

        internal int BodyGarmentCount
        {
            get { return bodyContent != null ? bodyContent.GarmentCount : 0; }
        }

        internal int BodyAttachmentCount
        {
            get { return bodyContent != null ? bodyContent.AttachmentCount : 0; }
        }

        // The content of the looks of the World visit, for tests.
        internal PlayerBodyContent BodyContent
        {
            get { return bodyContent; }
        }

        // The shared meshes and materials of the World visit, for the Editor's capture of the game
        // views, which dresses bodies of its own as a player's body is dressed.
        internal PlayerBodyMeshes BodyMeshes
        {
            get { return bodyMeshes; }
        }

        internal PlayerBodyMaterials BodyMaterials
        {
            get { return bodyMaterials; }
        }

        // Whether the World shows: the local player spawned, the content of its look loaded, and the
        // first view ready or its cap passed. Until then a cover hides the World.
        public bool IsWorldShown
        {
            get { return localPlayer != null && entry == EntryStage.Shown; }
        }

        // The frames in which the content of the local look was loaded and in which the World
        // showed, for tests; -1 before.
        internal int EntryLookFrame { get; private set; } = -1;

        internal int ShownFrame { get; private set; } = -1;

        // The cap of the first view, which tests shorten.
        internal float FirstViewCap
        {
            get { return firstViewCapSeconds; }
            set { firstViewCapSeconds = value; }
        }

        // The first view also waits for this part, such as the world actors' presentation, until
        // the cap.
        public void AddEntryReadiness(IWorldEntryReadiness readiness)
        {
            if (readiness != null && !entryReadiness.Contains(readiness))
            {
                entryReadiness.Add(readiness);
            }
        }

        // The lying body of a player corpse in the dead character's body model and look, dressed in
        // the garments of the items still in the corpse, or of the default model and look without
        // one; the key names the body for KeepCorpseBodies.
        public bool TryGetCorpseBody(RealtimeCharacterAppearance appearance, out string key, out Mesh mesh, out Material[] materials)
        {
            return TryGetCorpseBody(appearance, out key, out mesh, out materials, out _);
        }

        // As TryGetCorpseBody, and false with loading while the content of the look loads; the
        // lying body is baked once it is loaded and holds it while it exists.
        public bool TryGetCorpseBody(RealtimeCharacterAppearance appearance, out string key, out Mesh mesh,
            out Material[] materials, out bool loading)
        {
            EnsureBodyCaches();
            var slot = SlotOf(remotePlayerPrefab);
            var model = slot != null && slot.Models != null
                ? slot.Models.NamedOrDefault(appearance != null ? appearance.BodyModel : null)
                : null;
            var body = model != null ? model.Body : null;
            var look = RemotePlayerView.DressedLook(appearance, body != null ? body.Catalog : null, Presentation);
            key = PlayerCorpseBodies.KeyOf(model != null ? model.Name : null, look);
            return corpseBodies.TryGet(key, body, look, corpseMeshes, bodyMaterials, out mesh, out materials, out loading);
        }

        // Destroys the lying bodies that no corpse in view shows.
        public void KeepCorpseBodies(ICollection<string> keys)
        {
            corpseBodies.KeepOnly(keys);
        }

        // Adds where every body lies that shows its character dead, such as the body of a player
        // who left a corpse there, until the player respawns. One pass over the players serves
        // every corpse of a frame.
        public void CollectDeadBodies(List<Vector3> positions)
        {
            AddIfDead(localPlayer != null ? localPlayer.Body : null, positions);
            foreach (var remote in remotePlayers.Values)
            {
                if (remote != null)
                {
                    AddIfDead(remote.Body, positions);
                }
            }
        }

        // Whether one of the positions lies within the radius of the position, measured over the
        // ground.
        public static bool AnyNear(List<Vector3> positions, Vector3 position, float radius)
        {
            for (var index = 0; index < positions.Count; index++)
            {
                var offset = positions[index] - position;
                offset.y = 0f;
                if (offset.sqrMagnitude <= radius * radius)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddIfDead(CharacterLocomotionAnimator body, List<Vector3> positions)
        {
            if (body != null && body.IsDead && body.isActiveAndEnabled)
            {
                positions.Add(body.transform.position);
            }
        }

        // The presentation of another player by its network entity id.
        public bool TryGetRemotePlayer(ulong entityId, out RemotePlayerView view)
        {
            return remotePlayers.TryGetValue(entityId, out view) && view != null;
        }

        // The composition root passes the client session, the realtime session and the movement,
        // player, look, and admin channels, the local player source that this context fills, the
        // pointer focus, the body tint and look settings, and the local player's equipment when the
        // scene loads, before Start runs.
        public void Initialize(
            IClientSession accountSession,
            IRealtimeSession session,
            IRealtimeMovementChannel movement,
            IRealtimePlayerChannel players,
            IRealtimeLookChannel looks,
            IRealtimeAdminChannel admin,
            LocalPlayerSource localPlayerSource,
            WorldInputFocus focus,
            PlayerBodyTintSetting tintSetting,
            PlayerBodyLookSetting lookSetting,
            PlayerEquipment playerEquipment = null,
            PresentationBudget budget = null)
        {
            EnsureBodyCaches();
            clientSession = accountSession;
            realtimeSession = session;
            realtimeMovement = movement;
            realtimePlayers = players;
            realtimeLooks = looks;
            realtimeAdmin = admin;
            localPlayers = localPlayerSource;
            inputFocus = focus;
            bodyTint = tintSetting;
            bodyLook = lookSetting;
            equipment = playerEquipment;
            if (budget != null)
            {
                presentationBudget = budget;
            }

            if (preview == null)
            {
                preview = CharacterPreview.Create(transform);
            }
        }

        private void Awake()
        {
            EnsureBodyCaches();
        }

        // The body caches of the World visit: its content, and the meshes and materials that draw
        // with it.
        private void EnsureBodyCaches()
        {
            if (bodyContent != null)
            {
                return;
            }

            bodyContent = new PlayerBodyContent();
            bodyMaterials = new PlayerBodyMaterials(bodyContent);
            bodyMeshes = new PlayerBodyMeshes(true, bodyContent);
            corpseMeshes = new PlayerBodyMeshes(false, bodyContent);
        }

        private void Start()
        {
            if (!TryEnterWorld())
            {
                enabled = false;
            }
        }

        // The first entry into the World of the joined session: spawns the local player, binds
        // it to the movement session, and presents the players already in view. False when the
        // scene cannot enter a World. Start runs it once; a join that restores a lost connection
        // later binds the same player again in OnSessionJoined. The World shows once the content of
        // the local player's look is loaded and the first view is ready; see BeginEntry.
        internal bool TryEnterWorld()
        {
            EnsureBodyCaches();
            if (!ValidateReferences())
            {
                return false;
            }

            var world = clientSession != null ? clientSession.ActiveSimulationSession : null;
            if (world == null)
            {
                return false;
            }

            if (!WorldSceneCatalog.TryResolveScene(
                    world.worldId,
                    out var expectedSceneName,
                    out var sceneError))
            {
                Debug.LogError(
                    "WorldSceneContext cannot activate because the joined world cannot be resolved. "
                    + sceneError,
                    this);
                return false;
            }

            var activeSceneName = SceneManager.GetActiveScene().name;
            if (!string.Equals(
                    activeSceneName,
                    expectedSceneName,
                    System.StringComparison.Ordinal))
            {
                Debug.LogError(
                    "WorldSceneContext cannot activate because scene '" + activeSceneName
                    + "' does not match joined world '"
                    + world.worldId
                    + "', which requires scene '" + expectedSceneName + "'.",
                    this);
                return false;
            }

            if (realtimeSession == null
                || realtimeMovement == null
                || realtimePlayers == null
                || realtimeLooks == null
                || !realtimeSession.IsJoined
                || realtimeMovement.MovementSession == null)
            {
                Debug.LogError(
                    "WorldSceneContext cannot spawn the local player because the joined realtime movement session is unavailable.",
                    this);
                return false;
            }

            if (!TrySpawnLocalPlayer())
            {
                return false;
            }

            if (!localPlayer.EnableServerAuthoritativeMovement(realtimeSession, realtimeMovement, realtimeAdmin))
            {
                Debug.LogError(
                    "WorldSceneContext cannot start server-authoritative movement for the joined local player.",
                    this);
                DestroyLocalPlayer();
                return false;
            }

            CombineBody(localPlayer);
            ApplyLocalBodyTint();
            BeginEntry();
            if (bodyTint != null)
            {
                bodyTint.Changed += OnBodyTintChanged;
            }

            if (bodyLook != null)
            {
                OfferLocalBody();
                bodyLook.Changed += OnBodyLookChanged;
            }

            if (equipment != null)
            {
                equipment.Changed += OnEquipmentChanged;
                equipment.PresentationChanged += OnPresentationChanged;
            }

            realtimePlayers.EntitySpawned += OnEntitySpawned;
            realtimePlayers.EntityDespawned += OnEntityDespawned;
            realtimeLooks.EntityAppearanceChanged += OnEntityAppearanceChanged;
            realtimeMovement.SimulationSnapshotReceived += OnSimulationSnapshotReceived;
            realtimeSession.SessionEnded += OnSessionEnded;
            realtimeSession.SessionJoined += OnSessionJoined;
            foreach (var spawn in realtimePlayers.SpawnedEntities)
            {
                OnEntitySpawned(spawn);
            }

            return true;
        }

        // The World stays while a lost connection is restored: the local player stands where it
        // was, and its input neither moves it nor sends, until a join binds it again. The other
        // players went with the session's presence.
        private void OnSessionEnded(RealtimeClientError error)
        {
            if (localPlayer != null)
            {
                localPlayer.DisableServerAuthoritativeMovement();
            }
        }

        // A join that restores the lost connection binds the same local player to the new
        // movement session, where and as the worker says it stands: the body it resumed, or a new
        // entity at the saved place. The scene, the camera, and the player object stay, so the
        // local player source raises nothing; the other players spawn again as the worker sends
        // them.
        private void OnSessionJoined()
        {
            if (localPlayer == null)
            {
                return;
            }

            var world = clientSession != null ? clientSession.ActiveSimulationSession : null;
            var joined = realtimeSession.JoinedSession;
            if (world == null
                || joined == null
                || !string.Equals(world.worldId, joined.worldId, System.StringComparison.Ordinal))
            {
                return;
            }

            if (!localPlayer.EnableServerAuthoritativeMovement(realtimeSession, realtimeMovement, realtimeAdmin))
            {
                Debug.LogError(
                    "WorldSceneContext cannot bind the local player to the movement session of the join that restored the connection.",
                    this);
                return;
            }

            // The player stands where the worker placed it, so its loose cloth jumps along.
            ClothChainSolver.NotifyDiscontinuity(localPlayer.gameObject);
        }

        private void OnDestroy()
        {
            if (realtimeSession != null)
            {
                realtimeSession.SessionEnded -= OnSessionEnded;
                realtimeSession.SessionJoined -= OnSessionJoined;
            }

            if (realtimePlayers != null)
            {
                realtimePlayers.EntitySpawned -= OnEntitySpawned;
                realtimePlayers.EntityDespawned -= OnEntityDespawned;
            }

            if (realtimeLooks != null)
            {
                realtimeLooks.EntityAppearanceChanged -= OnEntityAppearanceChanged;
            }

            if (realtimeMovement != null)
            {
                realtimeMovement.SimulationSnapshotReceived -= OnSimulationSnapshotReceived;
            }

            if (bodyTint != null)
            {
                bodyTint.Changed -= OnBodyTintChanged;
            }

            if (bodyLook != null)
            {
                bodyLook.Changed -= OnBodyLookChanged;
                bodyLook.Offer(null, null);
            }

            if (equipment != null)
            {
                equipment.Changed -= OnEquipmentChanged;
                equipment.PresentationChanged -= OnPresentationChanged;
            }

            foreach (var remotePlayer in remotePlayers.Values)
            {
                if (remotePlayer != null)
                {
                    Destroy(remotePlayer.gameObject);
                }
            }

            remotePlayers.Clear();
            deferredAppearances.Clear();
            waitingBodies.Clear();
            warmUp?.Dispose(bodyMeshes);
            warmUp = null;
            UncoverWorld();
            corpseBodies.Dispose();
            bodyMaterials?.Dispose();
            corpseMeshes?.Dispose();
            bodyMeshes?.Dispose();
            // The content goes last, once nothing that draws with it is left.
            bodyContent?.Dispose();

            DestroyLocalPlayer();
        }

        private void OnEntitySpawned(RealtimeEntitySpawn spawn)
        {
            var movementSession = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (movementSession == null || spawn.EntityId == movementSession.ControlledEntityId)
            {
                return;
            }

            if (spawn.Kind != RealtimeEntityKind.Player
                || !RemotePlayerView.SupportsArchetype(spawn.ArchetypeId))
            {
                Debug.LogWarning(
                    "WorldSceneContext cannot present unsupported entity archetype '"
                    + spawn.ArchetypeId + "' for network entity '" + spawn.EntityId + "'.",
                    this);
                return;
            }

            // Like a Mob, a remote player never loads collision chunks: the local player's chunks
            // ground it, and beyond them it shows its replicated height. Loading the chunks around
            // every far spawn cost milliseconds, and the next owner snapshot unloaded them again.
            if (!remotePlayers.TryGetValue(spawn.EntityId, out var remotePlayer))
            {
                // Every remote player is a root of the World scene, so Unity's transform jobs, such
                // as the cloth's, take the players' bones on their worker threads side by side.
                remotePlayer = Instantiate(remotePlayerPrefab);
                SceneManager.MoveGameObjectToScene(remotePlayer.gameObject, gameObject.scene);
                remotePlayer.Initialize(
                    spawn.EntityId,
                    spawn.PersistentId,
                    spawn.DisplayName,
                    spawn.ArchetypeId,
                    movementSession.Settings.TickRateHz,
                    movementSession.SnapshotRateHz,
                    movementSession.CollisionStream,
                    movementSession.Settings.CharacterCollision);
                remotePlayers.Add(spawn.EntityId, remotePlayer);
                waitingBodies.Set(
                    spawn.EntityId,
                    realtimeLooks.TryGetAppearance(spawn.EntityId, out var appearance) ? appearance : spawn.Appearance);
            }

            remotePlayer.PushSnapshot(spawn.ServerTick, spawn.InitialState);
        }

        private void OnEntityDespawned(RealtimeEntityDespawn despawn)
        {
            if (!remotePlayers.TryGetValue(despawn.EntityId, out var remotePlayer))
            {
                return;
            }

            remotePlayers.Remove(despawn.EntityId);
            deferredAppearances.Remove(despawn.EntityId);
            waitingBodies.Remove(despawn.EntityId);
            Destroy(remotePlayer.gameObject);
        }

        // The local player's look comes through the look setting. A player whose body waits gets
        // the new look when its body is put on; a body that shows keeps its look until the content
        // and the combination of the new one are ready.
        private void OnEntityAppearanceChanged(RealtimeEntityAppearanceChanged change)
        {
            if (waitingBodies.Contains(change.EntityId))
            {
                waitingBodies.Set(change.EntityId, change.Appearance);
                return;
            }

            if (remotePlayers.TryGetValue(change.EntityId, out var remotePlayer) && remotePlayer != null)
            {
                ShowWhenBuilt(remotePlayer, change.Appearance);
            }
        }

        // Takes the content that loaded, makes the meshes of the finished builds and starts more,
        // then puts bodies on the players that wait for one, the nearest to the camera first, while
        // the frame's presentation budget lasts and at least one body a frame. A body whose content
        // is not loaded or whose combination is not built yet waits for it: its content loads and
        // its build runs on a worker thread meanwhile, so no frame waits for a load or builds a
        // mesh, and no body shows without the content of its look.
        private void PresentWaitingBodies()
        {
            presentationBudget.Begin();
            try
            {
                bodyContent.Update(presentationBudget);
                bodyMeshes.Update(presentationBudget);
                corpseMeshes.Update();
                bodyMaterials.Update();
                if (waitingBodies.Count > 0)
                {
                    PresentNearestBodies();
                }
            }
            finally
            {
                presentationBudget.End();
            }
        }

        private void PresentNearestBodies()
        {
            // The local player's camera, which renders the World.
            var camera = localPlayer != null && localPlayer.PlayerCamera != null
                ? localPlayer.PlayerCamera.Camera
                : null;
            var eye = camera != null ? camera.transform.position : Vector3.zero;
            waitingBodies.Touch(bodyMeshes, bodyContent);
            waitingBodies.OrderFrom(remotePlayers, eye);
            // Under the entry cover every waiting player asks for its content at once, which the
            // first view waits for, and the bodies still go on within the budget.
            var covered = entry != EntryStage.Shown;
            var asked = false;
            var presented = false;
            while (waitingBodies.TryTakeNext(out var nearest, out var appearance, out var content, out var wanted))
            {
                // Finding which content and mesh a look needs costs too; once a frame asked once and
                // the budget is spent, the farther players ask next frame.
                var asks = content == null || wanted == null;
                if (asks && asked && presentationBudget.IsSpent && !covered)
                {
                    waitingBodies.Wait(nearest.NetworkEntityId, appearance, content, wanted);
                    return;
                }

                asked |= asks;
                if (!IsBuilt(nearest, appearance, ref content, ref wanted)
                    || (covered && presented && presentationBudget.IsSpent))
                {
                    waitingBodies.Wait(nearest.NetworkEntityId, appearance, content, wanted);
                    continue;
                }

                PresentAppearance(nearest, appearance);
                presented = true;
                if (presentationBudget.IsSpent && !covered)
                {
                    return;
                }
            }
        }

        // Whether the content of the look the player shows is loaded and its combination built;
        // until then the content is asked for, and once it is loaded the build. A body that lies
        // dead keeps the look it had and needs none.
        private bool IsBuilt(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance,
            ref PlayerBodyContent.Needs content, ref PlayerBodyMeshes.Combined wanted)
        {
            var body = remotePlayer.Body;
            if (remotePlayer.Appearance != null && body != null && body.IsDead)
            {
                return true;
            }

            // While the content loads, asking costs nothing more.
            if (content != null && !bodyContent.Want(content))
            {
                return false;
            }

            if (content == null || wanted == null || wanted.Forgotten)
            {
                var slot = SlotOf(remotePlayerPrefab);
                var model = slot != null && slot.Models != null
                    ? slot.Models.NamedOrDefault(appearance != null ? appearance.BodyModel : null)
                    : null;
                if (model == null || model.Body == null)
                {
                    return true;
                }

                var look = RemotePlayerView.DressedLook(appearance, model.Body.Catalog, Presentation);
                if (content == null)
                {
                    content = PlayerBodyContent.NeedsOf(model.Body.Catalog, look);
                    if (!bodyContent.Want(content))
                    {
                        return false;
                    }
                }

                wanted = bodyMeshes.Want(model.Body, look);
            }
            else
            {
                bodyMeshes.Touch(wanted);
            }

            return wanted.IsReady;
        }

        // Shows the look now when its content is loaded and its combination built, and otherwise
        // once they are, in the meantime showing the body as it was.
        private void ShowWhenBuilt(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance)
        {
            PlayerBodyContent.Needs content = null;
            PlayerBodyMeshes.Combined wanted = null;
            if (IsBuilt(remotePlayer, appearance, ref content, ref wanted))
            {
                PresentAppearance(remotePlayer, appearance);
            }
            else
            {
                waitingBodies.Wait(remotePlayer.NetworkEntityId, appearance, content, wanted);
            }
        }

        // Shows the body model and the dressed look, or keeps them for later while the body shows
        // its character dead in the look it had.
        private void PresentAppearance(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance)
        {
            using (PresentBodyMarker.Auto())
            {
                PresentAppearanceNow(remotePlayer, appearance);
            }
        }

        private void PresentAppearanceNow(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance)
        {
            var body = remotePlayer.Body;
            if (remotePlayer.Appearance != null && body != null && body.IsDead)
            {
                deferredAppearances[remotePlayer.NetworkEntityId] = appearance;
                return;
            }

            deferredAppearances.Remove(remotePlayer.NetworkEntityId);
            if (remotePlayer.ShowBody(appearance != null ? appearance.BodyModel : null) || remotePlayer.Appearance == null)
            {
                CombineBody(remotePlayer);
                remotePlayer.ApplyBodyTint(BodiesTinted, bodyMaterials);
            }

            remotePlayer.ApplyAppearance(appearance, Presentation, bodyMaterials);
        }

        // Players that came into view get their bodies, and a body that lives again shows the look
        // it waited with.
        private void Update()
        {
            warmUp?.Update();
            PresentWaitingBodies();
            if (entry != EntryStage.Shown)
            {
                ContinueEntry();
                if (entry == EntryStage.LoadingLook)
                {
                    return;
                }
            }

            if (localLookDeferred && !IsLocalBodyDead)
            {
                localLookDeferred = false;
                OnBodyLookChanged();
            }

            if (localLookWaiting && IsWantedReady(localWantedContent, localWanted))
            {
                localLookWaiting = false;
                OnBodyLookChanged();
            }

            if (previewWaiting && IsWantedReady(previewWantedContent, previewWanted))
            {
                previewWaiting = false;
                ShowPreview(false);
            }

            if (deferredAppearances.Count == 0)
            {
                return;
            }

            revivedEntities.Clear();
            foreach (var deferred in deferredAppearances)
            {
                if (!remotePlayers.TryGetValue(deferred.Key, out var remotePlayer)
                    || remotePlayer == null
                    || remotePlayer.Body == null
                    || !remotePlayer.Body.IsDead)
                {
                    revivedEntities.Add(deferred.Key);
                }
            }

            foreach (var entityId in revivedEntities)
            {
                var appearance = deferredAppearances[entityId];
                deferredAppearances.Remove(entityId);
                if (remotePlayers.TryGetValue(entityId, out var remotePlayer) && remotePlayer != null)
                {
                    ShowWhenBuilt(remotePlayer, appearance);
                }
            }
        }

        // Whether a look that waits can be asked for again: its content loaded, then its combination
        // built or forgotten. Keeps both asked for meanwhile.
        private bool IsWantedReady(PlayerBodyContent.Needs content, PlayerBodyMeshes.Combined wanted)
        {
            if (wanted == null)
            {
                return bodyContent.Want(content);
            }

            bodyContent.Touch(content);
            bodyMeshes.Touch(wanted);
            return wanted.IsReady || wanted.Forgotten;
        }

        // The World is entered in two stages under the cover. Once the content of the local player's
        // look, of the preview's, and of every default look is loaded, the warm-up prepares the
        // bodies and draws them, and the local body and the preview show their looks, so the World
        // never shows the local player without its look. Then the first view is prepared: the cover
        // stays while the warm-up draws, while a player in view waits for the content of its look,
        // which loads and draws its new material variants once, and while a readiness part, such as
        // the world actors' presentation with its prefabs, is not ready, but at most until
        // FirstViewCapSeconds after the start of the entry, so the frames after the World showed do
        // not pay for what the first view needed. Asks for the content every frame until then.
        private void BeginEntry()
        {
            entry = EntryStage.LoadingLook;
            entryWatch = System.Diagnostics.Stopwatch.StartNew();
            entryContent = PlayerBodyWarmUp.DefaultNeeds(PlayerModels());
            CoverWorld();
            ContinueEntry();
        }

        private void ContinueEntry()
        {
            if (entry == EntryStage.LoadingLook)
            {
                if (!IsEntryLookLoaded())
                {
                    return;
                }

                PrepareFirstView();
            }

            // The warm-up draws the bodies under the cover in the frame the look was loaded.
            if (entry != EntryStage.PreparingView || Time.frameCount <= EntryLookFrame)
            {
                return;
            }

            // The cloth world grows under the cover for the players in view whose looks may wear
            // loose cloth, since growing writes every body in it again, and the World shows in a
            // later frame than one in which it grew.
            if (ClothChainScheduler.Reserve(waitingBodies.CountMayWearCloth()))
            {
                return;
            }

            var capped = entryWatch.Elapsed.TotalSeconds >= firstViewCapSeconds;
            var waitingPlayers = waitingBodies.CountWaitingForContent(bodyContent);
            var actorsReady = AreReadinessPartsReady();
            var warm = warmUp == null || warmUp.IsIdle;
            if (!capped && (waitingPlayers > 0 || !actorsReady || !warm))
            {
                return;
            }

            ShowWorld(waitingPlayers, actorsReady);
        }

        // Whether the content of the local look, of the preview's, and of every default look is
        // loaded; asks for it meanwhile.
        private bool IsEntryLookLoaded()
        {
            var ready = true;
            foreach (var needs in entryContent)
            {
                ready &= bodyContent.Want(needs);
            }

            var slot = SlotOf(localPlayer);
            var model = slot != null && slot.Models != null && bodyLook != null ? slot.Models.NamedOrDefault(bodyLook.Model) : null;
            if (model != null && model.Body != null && localEntryContent == null)
            {
                localEntryContent = PlayerBodyContent.NeedsOf(model.Body.Catalog, DressLocal(model.Body.Catalog, true));
                previewEntryContent = PlayerBodyContent.NeedsOf(model.Body.Catalog, DressLocal(model.Body.Catalog, false));
            }

            ready &= bodyContent.Want(localEntryContent);
            ready &= bodyContent.Want(previewEntryContent);
            return ready;
        }

        // The warm-up prepares and draws the bodies under the cover, and the local body and the
        // preview show the look they entered with, also when the character lies dead already.
        private void PrepareFirstView()
        {
            using (FirstViewMarker.Auto())
            {
                entry = EntryStage.PreparingView;
                entryLookMilliseconds = entryWatch.Elapsed.TotalMilliseconds;
                EntryLookFrame = Time.frameCount;
                WarmUpBodies();
                var appearance = LocalAppearance;
                if (appearance != null && bodyLook != null)
                {
                    appearance.ApplyLook(DressLocal(appearance.Catalog, true), bodyMaterials);
                }

                ShowPreview(true);
                entryContent = null;
                localEntryContent = null;
                previewEntryContent = null;
            }
        }

        private bool AreReadinessPartsReady()
        {
            for (var index = 0; index < entryReadiness.Count; index++)
            {
                var part = entryReadiness[index];
                if (part != null && !(part is Object component && component == null) && !part.IsReadyForEntry)
                {
                    return false;
                }
            }

            return true;
        }

        private void ShowWorld(int waitingPlayers, bool actorsReady)
        {
            entry = EntryStage.Shown;
            ShownFrame = Time.frameCount;
            UncoverWorld();
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var shown = entryWatch.Elapsed.TotalMilliseconds;
            var view = waitingPlayers == 0 && actorsReady
                ? "the first view with the " + remotePlayers.Count + " players in view was ready"
                : "the first view showed at its cap of " + (firstViewCapSeconds * 1000f).ToString("0", invariant) + " ms with "
                    + waitingPlayers + " players waiting for the content of their looks"
                    + (actorsReady ? string.Empty : " and the world actors not ready");
            ClientLog.Info(
                ClientLogCategory.Client,
                "Entered the World " + shown.ToString("0.0", invariant)
                    + " ms after the scene started: the content of the local look was loaded after "
                    + entryLookMilliseconds.ToString("0.0", invariant) + " ms, and " + view + ".");
        }

        // A camera after the player camera that draws black over the World while it is entered;
        // the screen-space interface draws over it.
        private void CoverWorld()
        {
            if (entryCover != null || playerCamera == null)
            {
                return;
            }

            entryCover = new GameObject("WorldEntryCover");
            entryCover.transform.SetParent(transform, false);
            var cover = entryCover.AddComponent<Camera>();
            cover.clearFlags = CameraClearFlags.SolidColor;
            cover.backgroundColor = Color.black;
            cover.cullingMask = 0;
            cover.allowHDR = false;
            cover.allowMSAA = false;
            cover.depth = playerCamera.depth + 100f;
        }

        private void UncoverWorld()
        {
            if (entryCover == null)
            {
                return;
            }

            entryCover.SetActive(false);
            Destroy(entryCover);
            entryCover = null;
        }

        // Equipment the local player put on or took off, or the tool it uses, dresses only its
        // own body: the other players wear what the server sends for them.
        private void OnEquipmentChanged()
        {
            ApplyLocalBodyLook();
        }

        // A presentation that arrived, such as the catalog that loaded after players came into
        // view, names the garments of every item, so it dresses every other player again: each
        // waits for its new look, which the frames put on as they put on bodies, the nearest first.
        private void OnPresentationChanged()
        {
            // The looks the waiting players asked content and meshes for may wear other garments now.
            waitingBodies.ForgetWanted();
            foreach (var remotePlayer in remotePlayers.Values)
            {
                if (remotePlayer != null && remotePlayer.Appearance != null
                    && !deferredAppearances.ContainsKey(remotePlayer.NetworkEntityId)
                    && !waitingBodies.Contains(remotePlayer.NetworkEntityId))
                {
                    waitingBodies.Wait(remotePlayer.NetworkEntityId, remotePlayer.Appearance, null, null);
                }
            }
        }

        private ItemPresentationCatalogIndex Presentation
        {
            get { return equipment != null ? equipment.Presentation : null; }
        }

        private bool IsLocalBodyDead
        {
            get
            {
                var body = localPlayer != null ? localPlayer.Body : null;
                return body != null && body.IsDead;
            }
        }

        // Moves remote players. The world actor presentation moves the Mobs of the same
        // snapshot, and every other id is ignored.
        private void OnSimulationSnapshotReceived(RealtimeSimulationSnapshot snapshot)
        {
            var movementSession = realtimeMovement != null ? realtimeMovement.MovementSession : null;
            if (movementSession == null)
            {
                return;
            }

            for (var index = 0; index < snapshot.Entities.Length; index++)
            {
                var entity = snapshot.Entities[index];
                if (entity.EntityId == movementSession.ControlledEntityId)
                {
                    continue;
                }

                if (!remotePlayers.TryGetValue(entity.EntityId, out var remotePlayer))
                {
                    continue;
                }

                remotePlayer.PushSnapshot(snapshot.ServerTick, entity);
            }
        }

        private bool BodiesTinted
        {
            get { return bodyTint != null && bodyTint.Enabled; }
        }

        private void CombineBody(Component player)
        {
            var appearance = player.GetComponentInChildren<PlayerBodyAppearance>(true);
            if (appearance != null)
            {
                appearance.CombineParts(bodyMeshes);
            }
        }

        private void OnBodyTintChanged()
        {
            ApplyLocalBodyTint();
            foreach (var remotePlayer in remotePlayers.Values)
            {
                if (remotePlayer != null)
                {
                    remotePlayer.ApplyBodyTint(BodiesTinted, bodyMaterials);
                }
            }
        }

        private PlayerBodyAppearance LocalAppearance
        {
            get
            {
                return localPlayer != null
                    ? localPlayer.GetComponentInChildren<PlayerBodyAppearance>(true)
                    : null;
            }
        }

        private void ApplyLocalBodyTint()
        {
            var appearance = LocalAppearance;
            if (appearance != null)
            {
                appearance.ApplyTint(BodiesTinted, PlayerBodyColors.LocalPlayer, bodyMaterials);
            }
        }

        // Another model puts in another body, which is combined, tinted, and offered to the panel;
        // either way the body shows the look. A dead body keeps its look until it lives again, and
        // the body keeps its model and look until the combination of the new ones is built.
        private void OnBodyLookChanged()
        {
            if (entry == EntryStage.LoadingLook)
            {
                // The World waits for the new look's content instead.
                if (ShowLocalModel())
                {
                    CombineBody(localPlayer);
                    ApplyLocalBodyTint();
                    OfferLocalBody();
                }

                localEntryContent = null;
                previewEntryContent = null;
                return;
            }

            if (IsLocalBodyDead)
            {
                localLookDeferred = true;
                return;
            }

            if (!IsLocalLookBuilt())
            {
                return;
            }

            if (ShowLocalModel())
            {
                CombineBody(localPlayer);
                ApplyLocalBodyTint();
                OfferLocalBody();
            }

            ApplyLocalBodyLook();
        }

        // Puts in the body of the chosen model, or of the default model for none or an unknown
        // name; true when it put in a new body.
        private bool ShowLocalModel()
        {
            var slot = SlotOf(localPlayer);
            return slot != null
                && slot.Models != null
                && slot.Show(slot.Models.NamedOrDefault(bodyLook != null ? bodyLook.Model : null));
        }

        // The panel lists the models and the catalog of the body the local player shows.
        private void OfferLocalBody()
        {
            if (bodyLook != null)
            {
                var slot = SlotOf(localPlayer);
                bodyLook.Offer(
                    slot != null ? slot.Models : null,
                    LocalAppearance != null ? LocalAppearance.Catalog : null);
            }
        }

        private static PlayerBodySlot SlotOf(Component player)
        {
            return player != null ? player.GetComponentInChildren<PlayerBodySlot>(true) : null;
        }

        private static PlayerBodyModels.Model DefaultModelOf(Component playerPrefab)
        {
            var slot = SlotOf(playerPrefab);
            return slot != null && slot.Models != null ? slot.Models.Default : null;
        }

        // The character's look, dressed in the garments of the items it wears as the inventory
        // shows them, with its headwear as the server stores it, and holding the tool it uses. A
        // dead body keeps its look until it lives again. Unless now is asked for, as when the
        // player spawns while the World loads, the body keeps its look until the combination of
        // the new one is built.
        private void ApplyLocalBodyLook(bool now = false)
        {
            if (entry == EntryStage.LoadingLook)
            {
                localEntryContent = null;
                previewEntryContent = null;
                return;
            }

            if (IsLocalBodyDead)
            {
                localLookDeferred = true;
                return;
            }

            if (!now && !IsLocalLookBuilt())
            {
                return;
            }

            var appearance = LocalAppearance;
            if (appearance != null && bodyLook != null)
            {
                appearance.ApplyLook(DressLocal(appearance.Catalog, true), bodyMaterials);
            }

            ShowPreview(now);
        }

        // The look of the local character for a body of the catalog, holding the tool it uses or
        // not, as the preview shows it.
        private BodyLook DressLocal(BodyAppearanceCatalog catalog, bool holdingTool)
        {
            return EquipmentGarments.Dress(
                bodyLook.Look,
                catalog,
                equipment != null ? equipment.Items : null,
                Presentation,
                bodyLook.HeadwearLowered,
                holdingTool && equipment != null ? equipment.ToolInUse : null);
        }

        // Whether the combination of the model and look the local player is to show is built;
        // when it is not, its build is asked for and the look waits for it.
        private bool IsLocalLookBuilt()
        {
            var slot = SlotOf(localPlayer);
            var model = slot != null && slot.Models != null && bodyLook != null
                ? slot.Models.NamedOrDefault(bodyLook.Model)
                : null;
            if (model == null || model.Body == null)
            {
                return true;
            }

            var look = DressLocal(model.Body.Catalog, true);
            localWantedContent = PlayerBodyContent.NeedsOf(model.Body.Catalog, look);
            if (!bodyContent.Want(localWantedContent))
            {
                localWanted = null;
                localLookWaiting = true;
                return false;
            }

            localWanted = bodyMeshes.Want(model.Body, look);
            localLookWaiting = !localWanted.IsReady;
            return !localLookWaiting;
        }

        // The preview shows the model of the local body in the same look, without the tool a
        // gathering character holds, once the combination of that look is built.
        private void ShowPreview(bool now)
        {
            var slot = SlotOf(localPlayer);
            var model = slot != null ? slot.Model : null;
            if (preview == null || model == null || model.Body == null || bodyLook == null)
            {
                return;
            }

            var look = DressLocal(model.Body.Catalog, false);
            if (!now)
            {
                previewWantedContent = PlayerBodyContent.NeedsOf(model.Body.Catalog, look);
                if (!bodyContent.Want(previewWantedContent))
                {
                    previewWanted = null;
                    previewWaiting = true;
                    return;
                }

                previewWanted = bodyMeshes.Want(model.Body, look);
                previewWaiting = !previewWanted.IsReady;
                if (previewWaiting)
                {
                    return;
                }
            }

            preview.Show(model, look, bodyMeshes, bodyMaterials);
        }

        // Prepares every body model a player can show before the World shows, so the first player
        // that comes into view costs what any other does.
        private void WarmUpBodies()
        {
            warmUp = PlayerBodyWarmUp.Begin(PlayerModels(), bodyMeshes, bodyMaterials, playerCamera, transform);
            ClientLog.Info(
                ClientLogCategory.Client,
                "Prepared " + warmUp.Models + " body models and " + warmUp.MaterialVariants
                    + " material variants for the World in "
                    + warmUp.Milliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms.");
        }

        // Every body model of the player prefabs.
        private List<PlayerBodyModels.Model> PlayerModels()
        {
            var models = new List<PlayerBodyModels.Model>();
            AddModels(SlotOf(remotePlayerPrefab), models);
            AddModels(SlotOf(localPlayerPrefab), models);
            return models;
        }

        private static void AddModels(PlayerBodySlot slot, List<PlayerBodyModels.Model> models)
        {
            if (slot == null || slot.Models == null)
            {
                return;
            }

            foreach (var model in slot.Models.Models)
            {
                if (model != null && model.Body != null && !models.Exists(known => known.Body == model.Body))
                {
                    models.Add(model);
                }
            }
        }

        private bool ValidateReferences()
        {
            if (localPlayerPrefab == null
                || playerSpawnPoint == null
                || remotePlayerPrefab == null)
            {
                Debug.LogError("WorldSceneContext requires a local player prefab, spawn point, and remote player prefab.", this);
                return false;
            }

            var localModel = DefaultModelOf(localPlayerPrefab);
            var remoteModel = DefaultModelOf(remotePlayerPrefab);
            if (localModel == null || localModel.Body == null || remoteModel == null || remoteModel.Body == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires the local and remote player prefabs to show a body model through a PlayerBodySlot.",
                    this);
                return false;
            }

            if (localPlayerPrefab.gameObject.scene.IsValid())
            {
                Debug.LogError(
                    "WorldSceneContext local player reference must be a prefab asset, not a scene instance.",
                    this);
                return false;
            }

            if (playerSpawnPoint.parent == null)
            {
                Debug.LogError(
                    "WorldSceneContext player spawn point must have a scene parent for runtime players.",
                    playerSpawnPoint);
                return false;
            }

            if (localPlayerPrefab.PlayerCamera == null
                || localPlayerPrefab.GetComponent<LocalPlayerInput>() == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires the local player prefab to own its input and player camera.",
                    localPlayerPrefab);
                return false;
            }

            return true;
        }

        private bool TrySpawnLocalPlayer()
        {
            localPlayer = Instantiate(
                localPlayerPrefab,
                playerSpawnPoint.position,
                playerSpawnPoint.rotation,
                playerSpawnPoint.parent);
            localPlayer.name = localPlayerPrefab.name;
            ShowLocalModel();

            if (localPlayer.PlayerCamera == null
                || localPlayer.PlayerInput == null
                || !localPlayer.PlayerInput.Initialize())
            {
                Debug.LogError(
                    "WorldSceneContext cannot initialize the spawned local player input and camera.",
                    localPlayer);
                DestroyLocalPlayer();
                return false;
            }

            localPlayer.PlayerCamera.SetTarget(localPlayer.CameraTarget, localPlayer.PlayerInput);
            localPlayer.PlayerCamera.SetInputFocus(inputFocus);
            localPlayer.SetInputFocus(inputFocus);
            localPlayers?.Set(localPlayer);

            // The cloth is laid from the camera that renders the World.
            playerCamera = localPlayer.PlayerCamera.Camera;
            ClothChainScheduler.UseMainView(playerCamera);
            return true;
        }

        // Also when the scene already destroyed the player, the source forgets it.
        private void DestroyLocalPlayer()
        {
            localPlayers?.Clear(localPlayer);
            ClothChainScheduler.ForgetMainView(playerCamera);
            playerCamera = null;
            if (localPlayer == null)
            {
                return;
            }

            localPlayer.DisableServerAuthoritativeMovement();
            Destroy(localPlayer.gameObject);
            localPlayer = null;
        }
    }
}
