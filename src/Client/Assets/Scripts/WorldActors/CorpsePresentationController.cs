using System;
using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Actors;
using UnityEngine;

namespace Mmorpg.Client.WorldActors
{
    // One presentation per nearby corpse. A player corpse shows the dead character's body lying
    // in the last pose of Death, turned as its character faced, in its look and the garments still
    // in the corpse, and stays hidden while the dead body of its character still lies there. A Mob
    // corpse of a Mob with its own model, such as corpse.mob.feral_wolf, shows that model lying in
    // the last pose of Death, turned as the Mob died. While the Mob it replaces still falls there
    // it stays hidden, and then plays on the rest of that fall; until the model's prefab loads it
    // is a capsule. Every other corpse is a generic capsule.
    [DisallowMultipleComponent]
    public sealed class CorpsePresentationController : MonoBehaviour
    {
        private readonly Dictionary<Guid, GameObject> presentations =
            new Dictionary<Guid, GameObject>();
        private readonly Dictionary<Guid, Renderer> playerBodies = new Dictionary<Guid, Renderer>();
        // The lying body each player corpse shows, by the key of its model and look.
        private readonly Dictionary<Guid, string> playerBodyKeys = new Dictionary<Guid, string>();
        // The corpses that show their Mob's model, and those that wait for its prefab to load.
        private readonly HashSet<Guid> mobModels = new HashSet<Guid>();
        private readonly HashSet<Guid> awaitingModels = new HashSet<Guid>();
        // The Mob model corpses hidden while the Mob they replace still falls there.
        private readonly HashSet<Guid> awaitingFalls = new HashSet<Guid>();
        // The box around each Mob model lying in the last pose of Death, by its prefab.
        private readonly Dictionary<GameObject, Bounds> lyingBounds = new Dictionary<GameObject, Bounds>();
        private MaterialPropertyBlock colorProperties;
        private WorldActorPresentationController actorPresentations;
        private CorpseClientController controller;
        private Material primitiveMaterial;
        private WorldSceneContext sceneContext;

        [SerializeField]
        private GameObject corpsePrefab;

        // Called by the composition root before the component is activated. Without a World
        // scene context every player corpse is a capsule, and without the actor presentations,
        // which load the Mob models, every Mob corpse is one.
        public void Initialize(
            CorpseClientController corpses,
            Material runtimePrimitiveMaterial,
            WorldSceneContext worldSceneContext = null,
            WorldActorPresentationController worldActorPresentations = null)
        {
            controller = corpses;
            primitiveMaterial = runtimePrimitiveMaterial;
            sceneContext = worldSceneContext;
            actorPresentations = worldActorPresentations;
        }

        // A player corpse hides under the dead body that left it and shows once the character
        // respawned and the body is gone.
        private void Update()
        {
            ShowLoadedModels();
            TakeOverFalls();
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
                if (isBody)
                {
                    ShowLyingBody(corpse, presentation);
                }

                var isModel = mobModels.Contains(corpse.CorpseId);
                presentation.transform.position = new Vector3(
                    corpse.PositionX,
                    corpse.PositionY + (isBody || isModel ? 0f : 0.2f),
                    corpse.PositionZ);
                if (isBody || isModel)
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
                var renderer = isBody || isModel ? null : presentation.GetComponentInChildren<Renderer>();
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

            if (sceneContext != null)
            {
                sceneContext.KeepCorpseBodies(new HashSet<string>(playerBodyKeys.Values));
            }
        }

        // A corpse whose look changed, as when someone loots its cloak, shows the lying body of
        // the new look.
        private void ShowLyingBody(CorpsePresenceEntry corpse, GameObject presentation)
        {
            if (sceneContext == null
                || !sceneContext.TryGetCorpseBody(corpse.Appearance, out var key, out var mesh, out var materials)
                || (playerBodyKeys.TryGetValue(corpse.CorpseId, out var shown) && shown == key))
            {
                return;
            }

            playerBodyKeys[corpse.CorpseId] = key;
            presentation.GetComponent<MeshFilter>().sharedMesh = mesh;
            presentation.GetComponent<MeshRenderer>().sharedMaterials = materials;
            var box = presentation.GetComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size + new Vector3(0.1f, 0.1f, 0.1f);
        }

        private GameObject CreatePresentation(CorpsePresenceEntry corpse)
        {
            GameObject presentation;
            if (PlayerCorpseBodies.IsPlayerCorpse(corpse.PresentationKey)
                && sceneContext != null
                && sceneContext.TryGetCorpseBody(corpse.Appearance, out var key, out var mesh, out var materials))
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
                playerBodyKeys[corpse.CorpseId] = key;
            }
            else if (TryCreateMobModel(corpse, out presentation))
            {
                mobModels.Add(corpse.CorpseId);
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

        // The archetype of the Mob a corpse shows the model of, such as mob.feral_wolf for
        // corpse.mob.feral_wolf, or null for a player's corpse or the generic Mob remains.
        public static string MobArchetypeOf(string presentationKey)
        {
            const string Prefix = "corpse.";
            return WorldActorCorpseRules.IsMobCorpsePresentationKey(presentationKey)
                && !string.Equals(presentationKey, WorldActorCorpseRules.MobCorpsePresentationKey, StringComparison.Ordinal)
                ? presentationKey.Substring(Prefix.Length)
                : null;
        }

        // A Mob corpse shown as a capsule while its model loaded shows the model once it is ready.
        private void ShowLoadedModels()
        {
            if (awaitingModels.Count == 0 || actorPresentations == null || controller == null)
            {
                return;
            }

            var ready = false;
            foreach (var corpseId in awaitingModels.ToArray())
            {
                var corpse = controller.State.NearbyCorpses.FirstOrDefault(entry => entry.CorpseId == corpseId);
                if (corpse != null
                    && !actorPresentations.TryGetArchetypePrefab(MobArchetypeOf(corpse.PresentationKey), out _, out var loading)
                    && loading)
                {
                    continue;
                }

                awaitingModels.Remove(corpseId);
                DestroyPresentation(corpseId);
                ready = true;
            }

            if (ready)
            {
                Rebuild();
            }
        }

        // The Mob's own model lying dead, from the prefab of its archetype: the rest of the fall of
        // the Mob it replaces when that Mob just left there falling, otherwise the last pose of
        // Death; with a box around the lying model for clicks. False while the prefab loads, which
        // marks the corpse to show the model later, or when the Mob has no model.
        private bool TryCreateMobModel(CorpsePresenceEntry corpse, out GameObject presentation)
        {
            presentation = null;
            var archetype = MobArchetypeOf(corpse.PresentationKey);
            if (archetype == null || actorPresentations == null)
            {
                return false;
            }

            if (!actorPresentations.TryGetArchetypePrefab(archetype, out var prefab, out var loading))
            {
                if (loading)
                {
                    awaitingModels.Add(corpse.CorpseId);
                }

                return false;
            }

            presentation = Instantiate(prefab, transform);
            var position = new Vector3(corpse.PositionX, corpse.PositionY, corpse.PositionZ);
            presentation.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, corpse.YawDegrees, 0f));
            var creature = presentation.GetComponent<CreatureAnimator>();
            if (creature != null)
            {
                creature.LieDead(-1f);
                if (!lyingBounds.TryGetValue(prefab, out var bounds))
                {
                    bounds = LyingBounds(presentation);
                    lyingBounds.Add(prefab, bounds);
                }

                var box = presentation.AddComponent<BoxCollider>();
                box.center = bounds.center;
                box.size = bounds.size + new Vector3(0.1f, 0.1f, 0.1f);
                if (actorPresentations.TryTakeFall(archetype, position, out var secondsIntoDeath))
                {
                    creature.LieDead(secondsIntoDeath);
                }
                else if (actorPresentations.IsFallingNear(archetype, position))
                {
                    awaitingFalls.Add(corpse.CorpseId);
                    ShowModel(presentation, false);
                }
            }

            return true;
        }

        // A Mob model corpse shows once the Mob it replaces has left, on the rest of its fall.
        private void TakeOverFalls()
        {
            if (awaitingFalls.Count == 0 || actorPresentations == null || controller == null)
            {
                return;
            }

            foreach (var corpseId in awaitingFalls.ToArray())
            {
                var corpse = controller.State.NearbyCorpses.FirstOrDefault(entry => entry.CorpseId == corpseId);
                var archetype = corpse != null ? MobArchetypeOf(corpse.PresentationKey) : null;
                if (archetype == null || !presentations.TryGetValue(corpseId, out var presentation) || presentation == null)
                {
                    awaitingFalls.Remove(corpseId);
                    continue;
                }

                var position = new Vector3(corpse.PositionX, corpse.PositionY, corpse.PositionZ);
                if (actorPresentations.IsFallingNear(archetype, position))
                {
                    continue;
                }

                awaitingFalls.Remove(corpseId);
                var creature = presentation.GetComponent<CreatureAnimator>();
                if (creature != null && actorPresentations.TryTakeFall(archetype, position, out var secondsIntoDeath))
                {
                    creature.LieDead(secondsIntoDeath);
                }

                ShowModel(presentation, true);
            }
        }

        private static void ShowModel(GameObject presentation, bool visible)
        {
            foreach (var renderer in presentation.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = visible;
            }
        }

        // The box around a model in its current pose, in the model's own space.
        private static Bounds LyingBounds(GameObject model)
        {
            var skin = model.GetComponentInChildren<SkinnedMeshRenderer>();
            if (skin == null)
            {
                return new Bounds(Vector3.up * 0.3f, new Vector3(1f, 0.6f, 1.6f));
            }

            var baked = new Mesh();
            // Baked in the renderer's own space, scale included, so its transform adds the scale
            // of the model, such as the Feral Alpha's.
            skin.BakeMesh(baked, true);
            var toModel = model.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
            var vertices = baked.vertices;
            var bounds = new Bounds(toModel.MultiplyPoint3x4(vertices[0]), Vector3.zero);
            for (var index = 1; index < vertices.Length; index++)
            {
                bounds.Encapsulate(toModel.MultiplyPoint3x4(vertices[index]));
            }

            Destroy(baked);
            return bounds;
        }

        private void DestroyPresentation(Guid corpseId)
        {
            playerBodies.Remove(corpseId);
            playerBodyKeys.Remove(corpseId);
            mobModels.Remove(corpseId);
            awaitingFalls.Remove(corpseId);
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
