using System.Collections.Generic;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.Client.WorldActors;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // Where a combatant is presented: its root, the ground point, and its height above it.
    public readonly struct CombatantView
    {
        public CombatantView(Transform root, float height, string displayName)
        {
            Root = root;
            Height = height;
            DisplayName = displayName ?? string.Empty;
        }

        public Transform Root { get; }

        public float Height { get; }

        public string DisplayName { get; }

        public Vector3 Position
        {
            get { return Root.position; }
        }

        public Vector3 Top
        {
            get { return Root.position + (Vector3.up * Height); }
        }

        public Vector3 Center
        {
            get { return Root.position + (Vector3.up * (Height * 0.5f)); }
        }
    }

    // Finds the presentation of a combatant by network entity id in the open World scene:
    // the local player, another player, or a world actor. Presentation only; the worker
    // decides range and line of sight from its own state.
    public sealed class CombatantViewLocator
    {
        public const string LocalPlayerName = "You";
        private const float FallbackHeight = 1.8f;
        private static readonly Dictionary<ulong, RemotePlayerView> NoRemotePlayers =
            new Dictionary<ulong, RemotePlayerView>();

        private readonly RealtimeSimulationClient realtimeClient;
        private readonly WorldSceneContext sceneContext;
        private readonly WorldActorPresentationController actorPresentation;

        public CombatantViewLocator(
            RealtimeSimulationClient realtimeClient,
            WorldSceneContext sceneContext,
            WorldActorPresentationController actorPresentation)
        {
            this.realtimeClient = realtimeClient;
            this.sceneContext = sceneContext;
            this.actorPresentation = actorPresentation;
        }

        // The local player of the joined session, or null before it spawned.
        public LocalPlayerController LocalPlayer
        {
            get { return sceneContext != null ? sceneContext.LocalPlayer : null; }
        }

        public ulong ControlledEntityId
        {
            get
            {
                var session = realtimeClient != null ? realtimeClient.MovementSession : null;
                return session != null ? session.ControlledEntityId : 0;
            }
        }

        // The camera of the local player, which every screen-space presentation uses.
        public Camera PlayerCamera
        {
            get
            {
                var player = LocalPlayer;
                return player != null && player.PlayerCamera != null
                    ? player.PlayerCamera.Camera
                    : null;
            }
        }

        // The presentations of the other players in view.
        public Dictionary<ulong, RemotePlayerView>.ValueCollection RemotePlayers
        {
            get { return sceneContext != null ? sceneContext.RemotePlayers : NoRemotePlayers.Values; }
        }

        // A visible presentation of the combatant, or false when it has none right now,
        // such as a hidden dead Mob or an actor whose prefab still loads.
        public bool TryGetView(ulong entityId, out CombatantView view)
        {
            view = default(CombatantView);
            if (entityId == 0)
            {
                return false;
            }

            if (entityId == ControlledEntityId)
            {
                var player = LocalPlayer;
                if (player == null)
                {
                    return false;
                }

                view = new CombatantView(
                    player.transform,
                    realtimeClient.MovementSession.Settings.CharacterCollision.Height,
                    LocalPlayerName);
                return true;
            }

            if (sceneContext != null
                && sceneContext.TryGetRemotePlayer(entityId, out var remote)
                && remote.gameObject.activeInHierarchy)
            {
                view = new CombatantView(
                    remote.transform,
                    remote.TargetHeight > 0f ? remote.TargetHeight : FallbackHeight,
                    remote.DisplayName);
                return true;
            }

            if (actorPresentation != null
                && actorPresentation.TryGetView(entityId, out var actor)
                && actor.gameObject.activeInHierarchy)
            {
                view = new CombatantView(
                    actor.transform,
                    actor.TargetHeight > 0f ? actor.TargetHeight : FallbackHeight,
                    actor.TargetDisplayName);
                return true;
            }

            return false;
        }
    }
}
