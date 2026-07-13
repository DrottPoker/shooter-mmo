using System;
using System.Collections.Generic;
using ShooterMmo.GameProtocol;
using ShooterMmo.Networking;
using UnityEngine;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class WorldSceneContext : MonoBehaviour
    {
        [SerializeField] private LocalPlayerController localPlayer;
        [SerializeField] private Transform playerSpawnPoint;
        [SerializeField] private RemotePlayerView remotePlayerPrefab;

        private readonly Dictionary<string, RemotePlayerView> remotePlayers =
            new Dictionary<string, RemotePlayerView>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> staleRemotePlayerIds = new List<string>();
        private RealtimeWorldClient worldClient;

        private void Start()
        {
            if (!ValidateReferences())
            {
                enabled = false;
                return;
            }

            if (ShooterMmoClientSession.ActiveWorldSession != null)
            {
                worldClient = ShooterMmoClientBootstrap.WorldClient;
                if (worldClient == null
                    || worldClient.MovementSession == null
                    || !localPlayer.EnableServerAuthoritativeMovement(worldClient))
                {
                    Debug.LogError(
                        "WorldSceneContext cannot start server-authoritative movement because the realtime movement session is unavailable.",
                        this);
                    enabled = false;
                    return;
                }

                worldClient.WorldSnapshotReceived += OnWorldSnapshotReceived;
            }
            else
            {
                localPlayer.Teleport(playerSpawnPoint.position, playerSpawnPoint.rotation);
            }

            localPlayer.PlayerCamera.SetTarget(localPlayer.CameraTarget, localPlayer.PlayerInput);
        }

        private void Update()
        {
            staleRemotePlayerIds.Clear();
            foreach (var remotePlayer in remotePlayers)
            {
                if (remotePlayer.Value.SecondsSinceLastSnapshot > 3f)
                {
                    staleRemotePlayerIds.Add(remotePlayer.Key);
                }
            }

            for (var index = 0; index < staleRemotePlayerIds.Count; index++)
            {
                var characterId = staleRemotePlayerIds[index];
                var remotePlayer = remotePlayers[characterId];
                remotePlayers.Remove(characterId);
                Destroy(remotePlayer.gameObject);
            }
        }

        private void OnDestroy()
        {
            if (worldClient != null)
            {
                worldClient.WorldSnapshotReceived -= OnWorldSnapshotReceived;
            }

            if (localPlayer != null)
            {
                localPlayer.DisableServerAuthoritativeMovement();
            }
        }

        private void OnWorldSnapshotReceived(RealtimeWorldSnapshot snapshot)
        {
            var movementSession = worldClient != null ? worldClient.MovementSession : null;
            if (movementSession == null)
            {
                return;
            }

            for (var index = 0; index < snapshot.Players.Length; index++)
            {
                var player = snapshot.Players[index];
                if (string.Equals(
                    player.CharacterId,
                    movementSession.CharacterId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!remotePlayers.TryGetValue(player.CharacterId, out var remotePlayer))
                {
                    remotePlayer = Instantiate(remotePlayerPrefab, transform);
                    var delayTicks = Mathf.Max(
                        2,
                        Mathf.CeilToInt(movementSession.Settings.TickRateHz * 0.1f));
                    remotePlayer.Initialize(
                        player.CharacterId,
                        movementSession.Settings.TickRateHz,
                        delayTicks,
                        movementSession.CollisionWorld,
                        movementSession.Settings.CharacterCollision);
                    remotePlayers.Add(player.CharacterId, remotePlayer);
                }

                remotePlayer.PushSnapshot(snapshot.ServerTick, player.State);
            }
        }

        private bool ValidateReferences()
        {
            if (localPlayer == null || playerSpawnPoint == null || remotePlayerPrefab == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires a local player, spawn point, and remote player prefab.",
                    this);
                return false;
            }

            if (localPlayer.PlayerCamera == null)
            {
                Debug.LogError(
                    "WorldSceneContext requires the local player prefab to own its player camera.",
                    localPlayer);
                return false;
            }

            if (!localPlayer.PlayerInput.Initialize())
            {
                Debug.LogError(
                    "WorldSceneContext cannot initialize because the local player input is not configured.",
                    localPlayer);
                return false;
            }

            return true;
        }
    }
}
