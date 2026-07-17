using System;
using System.Collections.Generic;
using System.Linq;
using ShooterMmo.Diagnostics;
using ShooterMmo.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShooterMmo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class CorpsePresentationController : MonoBehaviour
    {
        private const float LocalInteractionRadius = 3f;
        private readonly Dictionary<Guid, GameObject> presentations =
            new Dictionary<Guid, GameObject>();
        private MaterialPropertyBlock colorProperties;
        private CorpseClientController controller;
        private Transform localPlayer;

        [SerializeField]
        private GameObject corpsePrefab;

        private void Awake()
        {
            colorProperties = new MaterialPropertyBlock();
        }

        private void Start()
        {
            BindController();
            FindLocalPlayer();
        }

        private void Update()
        {
            if (controller == null)
            {
                BindController();
            }

            if (localPlayer == null)
            {
                FindLocalPlayer();
            }

            if (controller == null
                || localPlayer == null
                || Keyboard.current == null
                || !Keyboard.current.eKey.wasPressedThisFrame
                || controller.State.ActiveView != null
                || controller.State.PendingOperationId != Guid.Empty)
            {
                return;
            }

            var closest = controller.State.NearbyCorpses
                .Select(corpse => new
                {
                    Corpse = corpse,
                    DistanceSquared = SquaredDistance(localPlayer.position, corpse)
                })
                .Where(candidate =>
                    candidate.DistanceSquared <= LocalInteractionRadius * LocalInteractionRadius)
                .OrderBy(candidate => candidate.DistanceSquared)
                .FirstOrDefault();
            if (closest == null)
            {
                return;
            }

            if (!controller.TryOpen(closest.Corpse.CorpseId, out var error))
            {
                ClientLog.Warning(
                    ClientLogCategory.Client,
                    "Corpse interaction was not submitted: " + error);
            }
        }

        private void OnDestroy()
        {
            if (controller != null)
            {
                controller.State.Changed -= Rebuild;
            }

            ClearPresentations();
        }

        private void BindController()
        {
            var candidate = ShooterMmoClientBootstrap.CorpseController;
            if (candidate == null || ReferenceEquals(candidate, controller))
            {
                return;
            }

            if (controller != null)
            {
                controller.State.Changed -= Rebuild;
            }

            controller = candidate;
            controller.State.Changed += Rebuild;
            Rebuild();
        }

        private void FindLocalPlayer()
        {
            var player = FindAnyObjectByType<LocalPlayerController>();
            localPlayer = player == null ? null : player.transform;
        }

        private void Rebuild()
        {
            if (controller == null)
            {
                return;
            }

            var activeIds = new HashSet<Guid>(
                controller.State.NearbyCorpses.Select(corpse => corpse.CorpseId));
            foreach (var staleId in presentations.Keys
                         .Where(corpseId => !activeIds.Contains(corpseId))
                         .ToArray())
            {
                DestroyPresentation(staleId);
            }

            foreach (var corpse in controller.State.NearbyCorpses)
            {
                if (!presentations.TryGetValue(corpse.CorpseId, out var presentation))
                {
                    presentation = CreatePresentation(corpse);
                    presentations.Add(corpse.CorpseId, presentation);
                }

                presentation.transform.position = new Vector3(
                    corpse.PositionX,
                    corpse.PositionY + 0.2f,
                    corpse.PositionZ);
                presentation.name = "Corpse_" + corpse.CorpseId.ToString("N")
                    + "_" + corpse.PresentationKey;
                var renderer = presentation.GetComponentInChildren<Renderer>();
                if (renderer != null)
                {
                    var color = corpse.IsEmpty
                        ? new Color(0.25f, 0.25f, 0.25f, 1f)
                        : new Color(0.35f, 0.16f, 0.10f, 1f);
                    renderer.GetPropertyBlock(colorProperties);
                    colorProperties.SetColor("_Color", color);
                    colorProperties.SetColor("_BaseColor", color);
                    renderer.SetPropertyBlock(colorProperties);
                }
            }
        }

        private GameObject CreatePresentation(CorpsePresenceEntry corpse)
        {
            GameObject presentation;
            if (corpsePrefab != null)
            {
                presentation = Instantiate(corpsePrefab, transform);
            }
            else
            {
                presentation = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                presentation.transform.SetParent(transform, false);
                presentation.transform.localScale = new Vector3(0.65f, 0.22f, 1.15f);
                presentation.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
                presentation.GetComponent<Collider>().enabled = false;
            }

            presentation.transform.position = new Vector3(
                corpse.PositionX,
                corpse.PositionY + 0.2f,
                corpse.PositionZ);
            return presentation;
        }

        private void DestroyPresentation(Guid corpseId)
        {
            if (!presentations.Remove(corpseId, out var presentation)
                || presentation == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(presentation);
            }
            else
            {
                DestroyImmediate(presentation);
            }
        }

        private void ClearPresentations()
        {
            foreach (var corpseId in presentations.Keys.ToArray())
            {
                DestroyPresentation(corpseId);
            }
        }

        private static float SquaredDistance(Vector3 player, CorpsePresenceEntry corpse)
        {
            var deltaX = player.x - corpse.PositionX;
            var deltaY = player.y - corpse.PositionY;
            var deltaZ = player.z - corpse.PositionZ;
            return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
        }
    }
}
