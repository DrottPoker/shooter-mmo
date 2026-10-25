using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Worlds;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using Mmorpg.WorldData.Items.Presentation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mmorpg.Client.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSceneContext : MonoBehaviour
    {
        [SerializeField] private LocalPlayerController localPlayerPrefab;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;
        [SerializeField] private Transform entityPresentationRoot;

        private readonly Dictionary<ulong, RemotePlayerView> remotePlayers =
            new Dictionary<ulong, RemotePlayerView>();
        private readonly PlayerBodyMaterials bodyMaterials = new PlayerBodyMaterials();
        private readonly PlayerBodyMeshes bodyMeshes = new PlayerBodyMeshes();
        private readonly PlayerCorpseBodies corpseBodies = new PlayerCorpseBodies();
        // Looks that wait for a dead body to live again: a dying body keeps its clothes until its
        // character respawns, though its items already lie in its corpse.
        private readonly Dictionary<ulong, RealtimeCharacterAppearance> deferredAppearances =
            new Dictionary<ulong, RealtimeCharacterAppearance>();
        private readonly List<ulong> revivedEntities = new List<ulong>();
        private bool localLookDeferred;
        private RealtimeSimulationClient simulationClient;
        private WorldPointerFocus pointerFocus;
        private PlayerBodyTintSetting bodyTint;
        private PlayerBodyLookSetting bodyLook;
        private PlayerEquipment equipment;
        private LocalPlayerController localPlayer;

        // The local player of the joined session, or null before it spawned.
        public LocalPlayerController LocalPlayer
        {
            get { return localPlayer; }
        }

        // The presentations of the other players in view.
        public Dictionary<ulong, RemotePlayerView>.ValueCollection RemotePlayers
        {
            get { return remotePlayers.Values; }
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
            return corpseBodies.TryGet(key, body, look, bodyMeshes, bodyMaterials, out mesh, out materials);
        }

        // Destroys the lying bodies that no corpse in view shows.
        public void KeepCorpseBodies(ICollection<string> keys)
        {
            corpseBodies.KeepOnly(keys);
        }

        // Whether a body that shows its character dead lies near the position, such as the body
        // of the player who left a corpse there, until the player respawns.
        public bool IsDeadBodyNear(Vector3 position, float radius)
        {
            var body = localPlayer != null ? localPlayer.Body : null;
            if (IsDeadNear(body, position, radius))
            {
                return true;
            }

            foreach (var remote in remotePlayers.Values)
            {
                if (remote != null && IsDeadNear(remote.Body, position, radius))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDeadNear(CharacterLocomotionAnimator body, Vector3 position, float radius)
        {
            if (body == null || !body.IsDead || !body.isActiveAndEnabled)
            {
                return false;
            }

            var offset = body.transform.position - position;
            offset.y = 0f;
            return offset.sqrMagnitude <= radius * radius;
        }

        // The presentation of another player by its network entity id.
        public bool TryGetRemotePlayer(ulong entityId, out RemotePlayerView view)
        {
            return remotePlayers.TryGetValue(entityId, out view) && view != null;
        }

        // The composition root passes the persistent realtime client, the pointer focus, the
        // body tint and look settings, and the local player's equipment when the scene loads,
        // before Start runs.
        public void Initialize(
            RealtimeSimulationClient realtimeClient,
            WorldPointerFocus focus,
            PlayerBodyTintSetting tintSetting,
            PlayerBodyLookSetting lookSetting,
            PlayerEquipment playerEquipment = null)
        {
            simulationClient = realtimeClient;
            pointerFocus = focus;
            bodyTint = tintSetting;
            bodyLook = lookSetting;
            equipment = playerEquipment;
        }

        private void Start()
        {
            if (!ValidateReferences())
            {
                enabled = false;
                return;
            }

            if (ClientSession.ActiveSimulationSession == null)
            {
                enabled = false;
                return;
            }

            if (!WorldSceneCatalog.TryResolveScene(
                    ClientSession.ActiveSimulationSession.worldId,
                    out var expectedSceneName,
                    out var sceneError))
            {
                Debug.LogError(
                    "WorldSceneContext cannot activate because the joined world cannot be resolved. "
                    + sceneError,
                    this);
                enabled = false;
                return;
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
                    + ClientSession.ActiveSimulationSession.worldId
                    + "', which requires scene '" + expectedSceneName + "'.",
                    this);
                enabled = false;
                return;
            }

            if (simulationClient == null
                || !simulationClient.IsJoined
                || simulationClient.MovementSession == null)
            {
                Debug.LogError(
                    "WorldSceneContext cannot spawn the local player because the joined realtime movement session is unavailable.",
                    this);
                enabled = false;
                return;
            }

            if (!TrySpawnLocalPlayer())
            {
                enabled = false;
                return;
            }

            if (!localPlayer.EnableServerAuthoritativeMovement(simulationClient))
            {
                Debug.LogError(
                    "WorldSceneContext cannot start server-authoritative movement for the joined local player.",
                    this);
                DestroyLocalPlayer();
                enabled = false;
                return;
            }

            CombineBody(localPlayer);
            ApplyLocalBodyTint();
            ApplyLocalBodyLook();
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
            }

            simulationClient.EntitySpawned += OnEntitySpawned;
            simulationClient.EntityDespawned += OnEntityDespawned;
            simulationClient.EntityAppearanceChanged += OnEntityAppearanceChanged;
            simulationClient.SimulationSnapshotReceived += OnSimulationSnapshotReceived;
            foreach (var spawn in simulationClient.SpawnedEntities)
            {
                OnEntitySpawned(spawn);
            }
        }

        private void OnDestroy()
        {
            if (simulationClient != null)
            {
                simulationClient.EntitySpawned -= OnEntitySpawned;
                simulationClient.EntityDespawned -= OnEntityDespawned;
                simulationClient.EntityAppearanceChanged -= OnEntityAppearanceChanged;
                simulationClient.SimulationSnapshotReceived -= OnSimulationSnapshotReceived;
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
            }

            remotePlayers.Clear();
            deferredAppearances.Clear();
            bodyMaterials.Dispose();
            corpseBodies.Dispose();
            bodyMeshes.Dispose();

            DestroyLocalPlayer();
        }

        private void OnEntitySpawned(RealtimeEntitySpawn spawn)
        {
            var movementSession = simulationClient != null ? simulationClient.MovementSession : null;
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

            if (!movementSession.TryEnsureCollisionChunks(
                    new[]
                    {
                        new SimulationVector3(
                            spawn.InitialState.PositionX,
                            spawn.InitialState.PositionY,
                            spawn.InitialState.PositionZ)
                    },
                    out var collisionError))
            {
                simulationClient.DisconnectForClientFailure(
                    "collision_stream_failed",
                    collisionError);
                return;
            }

            if (!remotePlayers.TryGetValue(spawn.EntityId, out var remotePlayer))
            {
                remotePlayer = Instantiate(remotePlayerPrefab, entityPresentationRoot);
                remotePlayer.Initialize(
                    spawn.EntityId,
                    spawn.PersistentId,
                    spawn.DisplayName,
                    spawn.ArchetypeId,
                    movementSession.Settings.TickRateHz,
                    RemoteMovementPresenter.InterpolationDelayTicks(
                        movementSession.Settings.TickRateHz,
                        movementSession.SnapshotRateHz),
                    movementSession.CollisionWorld,
                    movementSession.Settings.CharacterCollision);
                remotePlayers.Add(spawn.EntityId, remotePlayer);
                PresentAppearance(
                    remotePlayer,
                    simulationClient.TryGetAppearance(spawn.EntityId, out var appearance) ? appearance : spawn.Appearance);
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
            Destroy(remotePlayer.gameObject);
        }

        // The local player's look comes through the look setting.
        private void OnEntityAppearanceChanged(RealtimeEntityAppearanceChanged change)
        {
            if (remotePlayers.TryGetValue(change.EntityId, out var remotePlayer) && remotePlayer != null)
            {
                PresentAppearance(remotePlayer, change.Appearance);
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

        // A body that lives again shows the look it waited with.
        private void Update()
        {
            if (localLookDeferred && !IsLocalBodyDead)
            {
                localLookDeferred = false;
                OnBodyLookChanged();
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
                    PresentAppearance(remotePlayer, appearance);
                }
            }
        }

        // Equipment the local player put on or took off dresses its body; a presentation that
        // arrived dresses every player.
        private void OnEquipmentChanged()
        {
            ApplyLocalBodyLook();
            foreach (var remotePlayer in remotePlayers.Values)
            {
                if (remotePlayer != null && remotePlayer.Appearance != null
                    && !deferredAppearances.ContainsKey(remotePlayer.NetworkEntityId))
                {
                    PresentAppearance(remotePlayer, remotePlayer.Appearance);
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
            var movementSession = simulationClient != null ? simulationClient.MovementSession : null;
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

                remotePlayer.PushSnapshot(snapshot.ServerTick, entity.State);
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
        // either way the body shows the look. A dead body keeps its look until it lives again.
        private void OnBodyLookChanged()
        {
            if (IsLocalBodyDead)
            {
                localLookDeferred = true;
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
        // shows them, with its headwear as the server stores it. A dead body keeps its look until
        // it lives again.
        private void ApplyLocalBodyLook()
        {
            if (IsLocalBodyDead)
            {
                localLookDeferred = true;
                return;
            }

            var appearance = LocalAppearance;
            if (appearance != null && bodyLook != null)
            {
                appearance.ApplyLook(
                    EquipmentGarments.Dress(
                        bodyLook.Look,
                        appearance.Catalog,
                        equipment != null ? equipment.Items : null,
                        Presentation,
                        bodyLook.HeadwearLowered),
                    bodyMaterials);
            }
        }

        private bool ValidateReferences()
        {
            if (localPlayerPrefab == null
                || playerSpawnPoint == null
                || remotePlayerPrefab == null
                || entityPresentationRoot == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires a local player prefab, spawn point, remote player prefab, and entity presentation root.",
                    this);
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
            return true;
        }

        private void DestroyLocalPlayer()
        {
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
