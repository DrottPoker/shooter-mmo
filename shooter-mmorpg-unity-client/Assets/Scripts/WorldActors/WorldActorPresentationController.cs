using System.Collections.Generic;
using System.Linq;
using ShooterMmo.Diagnostics;
using ShooterMmo.GameProtocol;
using UnityEngine;

namespace ShooterMmo.WorldActors
{
    [DisallowMultipleComponent]
    public sealed class WorldActorPresentationController : MonoBehaviour
    {
        private readonly Dictionary<ulong, WorldActorView> presentations =
            new Dictionary<ulong, WorldActorView>();
        private readonly MaterialPropertyBlock materialProperties =
            new MaterialPropertyBlock();
        private readonly HashSet<string> missingArchetypes = new HashSet<string>();
        private WorldActorClientController actorController;
        private WorldActorPresentationRegistry presentationRegistry;
        private Transform presentationRoot;

        private void Start()
        {
            actorController = ShooterMmoClientBootstrap.WorldActorController;
            presentationRegistry = Resources.Load<WorldActorPresentationRegistry>(
                "ShooterMmo/WorldActors/WorldActorPresentationRegistry");
            presentationRoot = new GameObject("WorldActorPresentations").transform;
            presentationRoot.SetParent(transform, false);
            if (actorController != null)
            {
                actorController.State.Changed += Rebuild;
                Rebuild();
            }
        }

        private void OnDestroy()
        {
            if (actorController != null)
            {
                actorController.State.Changed -= Rebuild;
            }

            foreach (var entityId in presentations.Keys.ToArray())
            {
                DestroyPresentation(entityId);
            }
        }

        private void Rebuild()
        {
            if (actorController == null)
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
                    view = CreatePresentation(actor);
                    presentations.Add(actor.EntityId, view);
                }

                view.Apply(actor);
            }
        }

        private WorldActorView CreatePresentation(WorldActorClientEntry actor)
        {
            GameObject presentation;
            if (presentationRegistry != null
                && presentationRegistry.TryResolve(
                    actor.PresentationArchetypeId,
                    out var prefab))
            {
                presentation = Instantiate(prefab, presentationRoot);
            }
            else
            {
                if (missingArchetypes.Add(actor.PresentationArchetypeId))
                {
                    ClientLog.Warning(
                        ClientLogCategory.Client,
                        "World actor presentation archetype '"
                            + actor.PresentationArchetypeId
                            + "' is not mapped. Using the explicit development fallback.");
                }

                presentation = GameObject.CreatePrimitive(
                    actor.Kind == RealtimeWorldActorKind.Npc
                        ? PrimitiveType.Capsule
                        : PrimitiveType.Cube);
                presentation.transform.SetParent(presentationRoot, false);
                var existingCollider = presentation.GetComponent<Collider>();
                if (existingCollider != null)
                {
                    existingCollider.enabled = false;
                }
            }

            presentation.name = "WorldActor_" + actor.ActorDefinitionId
                + "_" + actor.EntityId;
            var view = presentation.GetComponent<WorldActorView>();
            if (view == null)
            {
                view = actor.Kind == RealtimeWorldActorKind.Npc
                    ? presentation.AddComponent<NpcView>()
                    : presentation.AddComponent<MobView>();
            }

            view.Initialize(actor);
            ApplyFallbackColor(presentation, actor);
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

            var color = actor.Kind == RealtimeWorldActorKind.Npc
                ? new Color(0.20f, 0.55f, 0.85f, 1f)
                : new Color(0.70f, 0.22f, 0.18f, 1f);
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
