using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Items;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    [DisallowMultipleComponent]
    public sealed class CorpsePresentationController : MonoBehaviour
    {
        private readonly Dictionary<Guid, GameObject> presentations =
            new Dictionary<Guid, GameObject>();
        private MaterialPropertyBlock colorProperties;
        private CorpseClientController controller;
        private Material primitiveMaterial;

        [SerializeField]
        private GameObject corpsePrefab;

        // Called by the composition root before the component is activated.
        public void Initialize(CorpseClientController corpses, Material runtimePrimitiveMaterial)
        {
            controller = corpses;
            primitiveMaterial = runtimePrimitiveMaterial;
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

                presentation.transform.position = new Vector3(
                    corpse.PositionX,
                    corpse.PositionY + 0.2f,
                    corpse.PositionZ);
                presentation.name = "Corpse_" + corpse.CorpseId.ToString("N")
                    + "_" + corpse.PresentationKey;
                var target = presentation.GetComponent<CorpseInteractionTarget>();
                if (target == null)
                {
                    target = presentation.AddComponent<CorpseInteractionTarget>();
                }

                target.Apply(corpse);
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
