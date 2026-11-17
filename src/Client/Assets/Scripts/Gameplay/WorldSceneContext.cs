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
        [SerializeField] private LocalPlayerController localPlayerPrefab;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;

        private readonly Dictionary<ulong, RemotePlayerView> remotePlayers =
            new Dictionary<ulong, RemotePlayerView>();
        private readonly PlayerBodyMaterials bodyMaterials = new PlayerBodyMaterials();
        // Nothing reads the shared meshes back, so they keep no copy once uploaded; the corpse
        // bakes read theirs back and take readable meshes of their own.
        private readonly PlayerBodyMeshes bodyMeshes = new PlayerBodyMeshes(true);
        private readonly PlayerBodyMeshes corpseMeshes = new PlayerBodyMeshes();
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
        // The local body's look, or the preview's, waits for the build of its combination.
        private bool localLookWaiting;
        private bool previewWaiting;
        private PlayerBodyMeshes.Combined localWanted;
        private PlayerBodyMeshes.Combined previewWanted;
        private PlayerBodyWarmUp warmUp;
        private bool warmUpEnded;
        private IClientSession clientSession;
        private IRealtimeSession realtimeSession;
        private IRealtimeMovementChannel realtimeMovement;
        private IRealtimePlayerChannel realtimePlayers;
        private IRealtimeLookChannel realtimeLooks;
        private IRealtimeAdminChannel realtimeAdmin;
        private LocalPlayerSource localPlayers;
        private WorldPointerFocus pointerFocus;
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

        // The combined meshes and the material copies that the shared body caches hold, which the
        // crowd measurement reads after a crowd left; nothing reads them a frame.
        internal int BodyMeshCount
        {
            get { return bodyMeshes.Count + corpseMeshes.Count; }
        }

        internal int BodyMaterialCount
        {
            get { return bodyMaterials.Count; }
        }

        // The lying body of a player corpse in the dead character's body model and look, dressed in
        // the garments of the items still in the corpse, or of the default model and look without
        // one; the key names the body for KeepCorpseBodies.
        public bool TryGetCorpseBody(RealtimeCharacterAppearance appearance, out string key, out Mesh mesh, out Material[] materials)
        {
            var slot = SlotOf(remotePlayerPrefab);
            var model = slot != null && slot.Models != null
                ? slot.Models.NamedOrDefault(appearance != null ? appearance.BodyModel : null)
                : null;
            var body = model != null ? model.Body : null;
            var look = RemotePlayerView.DressedLook(appearance, body != null ? body.Catalog : null, Presentation);
            key = PlayerCorpseBodies.KeyOf(model != null ? model.Name : null, look);
            return corpseBodies.TryGet(key, body, look, corpseMeshes, bodyMaterials, out mesh, out materials);
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
            WorldPointerFocus focus,
            PlayerBodyTintSetting tintSetting,
            PlayerBodyLookSetting lookSetting,
            PlayerEquipment playerEquipment = null,
            PresentationBudget budget = null)
        {
            clientSession = accountSession;
            realtimeSession = session;
            realtimeMovement = movement;
            realtimePlayers = players;
            realtimeLooks = looks;
            realtimeAdmin = admin;
            localPlayers = localPlayerSource;
            pointerFocus = focus;
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
        // later binds the same player again in OnSessionJoined.
        internal bool TryEnterWorld()
        {
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
            ApplyLocalBodyLook(true);
            WarmUpBodies();
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
            bodyMaterials.Dispose();
            corpseBodies.Dispose();
            corpseMeshes.Dispose();
            bodyMeshes.Dispose();

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
        // the new look when its body is put on.
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

        // Makes the meshes of the finished builds and starts more, then puts bodies on the players
        // that wait for one, the nearest to the camera first, while the frame's presentation
        // budget lasts and at least one body a frame. A body whose combination is not built yet
        // waits for it: its build runs on a worker thread meanwhile, so no frame builds a mesh.
        private void PresentWaitingBodies()
        {
            presentationBudget.Begin();
            try
            {
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
            waitingBodies.Touch(bodyMeshes);
            waitingBodies.OrderFrom(remotePlayers, eye);
            var asked = false;
            while (waitingBodies.TryTakeNext(out var nearest, out var appearance, out var wanted))
            {
                // Finding which mesh a look needs costs too; once a frame asked once and the budget
                // is spent, the farther players ask next frame.
                if (wanted == null && asked && presentationBudget.IsSpent)
                {
                    waitingBodies.Wait(nearest.NetworkEntityId, appearance, null);
                    return;
                }

                asked |= wanted == null;
                if (!IsBuilt(nearest, appearance, ref wanted))
                {
                    waitingBodies.Wait(nearest.NetworkEntityId, appearance, wanted);
                    continue;
                }

                PresentAppearance(nearest, appearance);
                if (presentationBudget.IsSpent)
                {
                    return;
                }
            }
        }

        // Whether the combination the player shows in the look is built; when it is not, its build
        // is asked for. A body that lies dead keeps the look it had and needs none.
        private bool IsBuilt(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance, ref PlayerBodyMeshes.Combined wanted)
        {
            var body = remotePlayer.Body;
            if (remotePlayer.Appearance != null && body != null && body.IsDead)
            {
                return true;
            }

            if (wanted == null || wanted.Forgotten)
            {
                var slot = SlotOf(remotePlayerPrefab);
                var model = slot != null && slot.Models != null
                    ? slot.Models.NamedOrDefault(appearance != null ? appearance.BodyModel : null)
                    : null;
                if (model == null || model.Body == null)
                {
                    return true;
                }

                wanted = bodyMeshes.Want(model.Body, RemotePlayerView.DressedLook(appearance, model.Body.Catalog, Presentation));
            }
            else
            {
                bodyMeshes.Touch(wanted);
            }

            return wanted.IsReady;
        }

        // Shows the look now when its combination is built, and otherwise once it is, in the
        // meantime showing the body as it was.
        private void ShowWhenBuilt(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance)
        {
            PlayerBodyMeshes.Combined wanted = null;
            if (IsBuilt(remotePlayer, appearance, ref wanted))
            {
                PresentAppearance(remotePlayer, appearance);
            }
            else
            {
                waitingBodies.Wait(remotePlayer.NetworkEntityId, appearance, wanted);
            }
        }

        // Shows the body model and the dressed look, or keeps them for later while the body shows
        // its character dead in the look it had.
        private void PresentAppearance(RemotePlayerView remotePlayer, RealtimeCharacterAppearance appearance)
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
            if (warmUp != null && !warmUpEnded)
            {
                warmUpEnded = warmUp.TryEnd();
            }

            PresentWaitingBodies();
            if (localLookDeferred && !IsLocalBodyDead)
            {
                localLookDeferred = false;
                OnBodyLookChanged();
            }

            if (localLookWaiting)
            {
                bodyMeshes.Touch(localWanted);
                if (localWanted == null || localWanted.IsReady || localWanted.Forgotten)
                {
                    localLookWaiting = false;
                    OnBodyLookChanged();
                }
            }

            if (previewWaiting)
            {
                bodyMeshes.Touch(previewWanted);
                if (previewWanted == null || previewWanted.IsReady || previewWanted.Forgotten)
                {
                    previewWaiting = false;
                    ShowPreview(false);
                }
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
            // The looks the waiting players asked meshes for may wear other garments now.
            waitingBodies.ForgetWanted();
            foreach (var remotePlayer in remotePlayers.Values)
            {
                if (remotePlayer != null && remotePlayer.Appearance != null
                    && !deferredAppearances.ContainsKey(remotePlayer.NetworkEntityId)
                    && !waitingBodies.Contains(remotePlayer.NetworkEntityId))
                {
                    waitingBodies.Wait(remotePlayer.NetworkEntityId, remotePlayer.Appearance, null);
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

            localWanted = bodyMeshes.Want(model.Body, DressLocal(model.Body.Catalog, true));
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
                previewWanted = bodyMeshes.Want(model.Body, look);
                previewWaiting = !previewWanted.IsReady;
                if (previewWaiting)
                {
                    return;
                }
            }

            preview.Show(model, look, bodyMeshes, bodyMaterials);
        }

        // Prepares every body model a player can show while the World loads, so the first player
        // that comes into view costs what any other does.
        private void WarmUpBodies()
        {
            var models = new List<PlayerBodyModels.Model>();
            AddModels(SlotOf(remotePlayerPrefab), models);
            AddModels(SlotOf(localPlayerPrefab), models);
            warmUp = PlayerBodyWarmUp.Begin(models, bodyMeshes, bodyMaterials, playerCamera, transform);
            ClientLog.Info(
                ClientLogCategory.Client,
                "Prepared " + warmUp.Models + " body models and " + warmUp.MaterialVariants
                    + " material variants for the World in "
                    + warmUp.Milliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms.");
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
            localPlayer.PlayerCamera.SetPointerFocus(pointerFocus);
            localPlayer.SetPointerFocus(pointerFocus);
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
