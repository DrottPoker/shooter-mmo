using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // One presentation per nearby corpse. A player corpse shows the body lying in the last pose
    // of Death, turned as its character faced, and stays hidden while the dead body of its
    // character still lies there; every other corpse is a generic capsule for now.
    [DisallowMultipleComponent]
    public sealed class CorpsePresentationController : MonoBehaviour
    {
        private readonly Dictionary<Guid, GameObject> presentations =
            new Dictionary<Guid, GameObject>();
        private readonly Dictionary<Guid, Renderer> playerBodies = new Dictionary<Guid, Renderer>();
        private MaterialPropertyBlock colorProperties;
        private CorpseClientController controller;
        private Material primitiveMaterial;
        private WorldSceneContext sceneContext;

        [SerializeField]
        private GameObject corpsePrefab;

        // Called by the composition root before the component is activated. Without a World
        // scene context every corpse is a capsule.
        public void Initialize(
            CorpseClientController corpses,
            Material runtimePrimitiveMaterial,
            WorldSceneContext worldSceneContext = null)
        {
            controller = corpses;
            primitiveMaterial = runtimePrimitiveMaterial;
            sceneContext = worldSceneContext;
        }

        // A player corpse hides under the dead body that left it and shows once the character
        // respawned and the body is gone.
        private void Update()
        {
            if (sceneContext == null)
            {
                return;
            }

            foreach (var body in playerBodies.Values)
            {
                if (body != null)
                {
                    body.enabled = !sceneContext.IsDeadBodyNear(body.transform.position, PlayerCorpseBodies.OwnBodyRadius);
                }
            }
        }

        private void Awake()
        {
            colorProperties = new MaterialPropertyBlock();
        }

        private void Start()
        {
            if (controller == null)
            {
                return;
            }

            controller.State.Changed += Rebuild;
            Rebuild();
        }

        private void OnDestroy()
        {
            if (controller != null)
            {
                controller.State.Changed -= Rebuild;
            }

            ClearPresentations();
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

                var isBody = playerBodies.ContainsKey(corpse.CorpseId);
                presentation.transform.position = new Vector3(
                    corpse.PositionX,
                    corpse.PositionY + (isBody ? 0f : 0.2f),
                    corpse.PositionZ);
                if (isBody)
                {
                    presentation.transform.rotation = Quaternion.Euler(0f, corpse.YawDegrees, 0f);
                }

                presentation.name = "Corpse_" + corpse.CorpseId.ToString("N")
                    + "_" + corpse.PresentationKey;
                var target = presentation.GetComponent<CorpseInteractionTarget>();
                if (target == null)
                {
                    target = presentation.AddComponent<CorpseInteractionTarget>();
                }

                target.Apply(corpse);
                var renderer = isBody ? null : presentation.GetComponentInChildren<Renderer>();
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
            if (PlayerCorpseBodies.IsPlayerCorpse(corpse.PresentationKey)
                && sceneContext != null
                && sceneContext.TryGetCorpseBody(out var mesh, out var materials))
            {
                presentation = new GameObject("PlayerCorpseBody");
                presentation.transform.SetParent(transform, false);
                presentation.AddComponent<MeshFilter>().sharedMesh = mesh;
                var bodyRenderer = presentation.AddComponent<MeshRenderer>();
                bodyRenderer.sharedMaterials = materials;
                bodyRenderer.enabled = false;
                var box = presentation.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size + new Vector3(0.1f, 0.1f, 0.1f);
                playerBodies[corpse.CorpseId] = bodyRenderer;
            }
            else if (corpsePrefab != null)
            {
                presentation = Instantiate(corpsePrefab, transform);
            }
            else
            {
                presentation = RuntimePrimitives.Create(PrimitiveType.Capsule, primitiveMaterial);
                presentation.transform.SetParent(transform, false);
                presentation.transform.localScale = new Vector3(0.65f, 0.22f, 1.15f);
                presentation.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            }

            var collider = presentation.GetComponentInChildren<Collider>();
            if (collider == null)
            {
                collider = presentation.AddComponent<BoxCollider>();
            }

            collider.enabled = true;

            presentation.transform.position = new Vector3(
                corpse.PositionX,
                corpse.PositionY + 0.2f,
                corpse.PositionZ);
            return presentation;
        }

        private void DestroyPresentation(Guid corpseId)
        {
            playerBodies.Remove(corpseId);
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

    }
}
