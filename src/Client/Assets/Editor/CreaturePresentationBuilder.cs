using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mmorpg.Client.Gameplay;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Mmorpg.Client.Editor
{
    // Builds the presentations of the creatures with clips of their own from their exported models,
    // after CreatureLocomotion: each model's Animator controller, with Idle, Locomotion (Walk and
    // Run blended by Speed at the playback rate LocomotionRate), Attack on the Attack trigger, and
    // Death while Dead, and the world actor prefabs that show it, such as the Feral Wolf's and the
    // larger, darker Feral Alpha's. The prefabs keep their assets, so the presentation registry and
    // Addressables keep naming them. A build writes a controller or a prefab only when it differs
    // from what the build makes, so building again without a change rewrites nothing. Run MMORPG >
    // Tools > Art Pipeline > Build Creature Presentations after an export of a creature changes its
    // clips.
    public static class CreaturePresentationBuilder
    {
        public const string MenuPath = "MMORPG/Tools/Art Pipeline/Build Creature Presentations";
        public const string WolfModel = "Assets/Art/Models/Creatures/Wolf.fbx";
        public const string WolfController = "Assets/Art/Animation/WolfLocomotion.controller";
        public const string FeralWolfPrefab = "Assets/Prefabs/WorldActors/Mob_FeralWolf.prefab";
        public const string FeralAlphaPrefab = "Assets/Prefabs/WorldActors/Mob_FeralAlpha.prefab";
        // The Feral Alpha's darker coat: the wolf's painted texture, tinted. The Feral Wolf shows
        // the wolf's own material, so its placeholder material is removed.
        public const string FeralAlphaMaterial = "Assets/Art/Materials/WorldActors/M_Mob_FeralAlpha.mat";
        public const string FeralWolfPlaceholderMaterial = "Assets/Art/Materials/WorldActors/M_Mob_FeralWolf.mat";
        public const float FeralAlphaScale = 1.3f;
        public static readonly Color FeralAlphaTint = new Color(0.58f, 0.55f, 0.56f, 1f);
        // The model's child in every creature prefab.
        public const string ModelName = "Model";

        // The speeds the wolf's Walk and Run clips are made for (art/Creatures/Wolf).
        public const float WolfWalkSpeed = 1.4f;
        public const float WolfRunSpeed = 4.5f;

        [MenuItem(MenuPath)]
        public static void BuildMenu()
        {
            Build();
            Debug.Log("Built the creature presentations.");
        }

        // For a batch-mode Unity: -executeMethod Mmorpg.Client.Editor.CreaturePresentationBuilder.Build
        public static void Build()
        {
            ArtMaterialCreator.CreateMissingMaterials();
            var controller = BuildController(WolfModel, WolfController, WolfWalkSpeed, WolfRunSpeed);
            var alpha = BuildAlphaMaterial();
            BuildPrefab(FeralWolfPrefab, WolfModel, controller, 1f, null);
            BuildPrefab(FeralAlphaPrefab, WolfModel, controller, FeralAlphaScale, alpha);
            AssetDatabase.DeleteAsset(FeralWolfPlaceholderMaterial);
            AssetDatabase.SaveAssets();
        }

        public static AnimationClip Clip(string modelPath, string name)
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>()
                .FirstOrDefault(candidate => candidate.name == name);
            if (clip == null)
            {
                throw new InvalidOperationException(modelPath + " has no clip " + name + "; export the creature first.");
            }

            return clip;
        }

        // The controller at the path, built again only when it differs from the one the model's clips
        // make, so the prefabs keep naming the same asset.
        public static AnimatorController BuildController(string modelPath, string controllerPath, float walkSpeed, float runSpeed)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (existing != null)
            {
                var wanted = new AnimatorController();
                try
                {
                    wanted.AddLayer("Base Layer");
                    Fill(wanted, modelPath, walkSpeed, runSpeed, false);
                    if (Describe(existing) == Describe(wanted))
                    {
                        return existing;
                    }
                }
                finally
                {
                    DestroyInMemory(wanted);
                }
            }

            AssetDatabase.DeleteAsset(controllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            Fill(controller, modelPath, walkSpeed, runSpeed, true);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        // Everything a build sets on a controller, in a fixed order, so two controllers compare as
        // text: the parameters, and per layer its default state, its states with their motions and
        // speeds, and every transition with its timing and conditions.
        public static string Describe(AnimatorController controller)
        {
            var text = new StringBuilder();
            foreach (var parameter in controller.parameters)
            {
                text.Append("parameter ").Append(parameter.name).Append(' ').Append(parameter.type).Append(' ')
                    .Append(parameter.defaultFloat).Append(' ').Append(parameter.defaultBool).Append(' ').Append(parameter.defaultInt).Append('\n');
            }

            foreach (var layer in controller.layers)
            {
                var machine = layer.stateMachine;
                text.Append("layer ").Append(layer.name).Append(" default ").Append(machine.defaultState != null ? machine.defaultState.name : "-").Append('\n');
                foreach (var state in machine.states.Select(child => child.state).OrderBy(state => state.name, StringComparer.Ordinal))
                {
                    text.Append("state ").Append(state.name).Append(' ').Append(Describe(state.motion)).Append(" speed ").Append(state.speed)
                        .Append(' ').Append(state.speedParameterActive ? state.speedParameter : "-").Append('\n');
                    foreach (var transition in state.transitions)
                    {
                        Describe(text, state.name, transition);
                    }
                }

                foreach (var transition in machine.anyStateTransitions)
                {
                    Describe(text, "Any State", transition);
                }
            }

            return text.ToString();
        }

        private static string Describe(Motion motion)
        {
            if (motion is BlendTree tree)
            {
                return "tree " + tree.blendType + " " + tree.blendParameter + " ("
                    + string.Join(", ", tree.children.Select(child => Describe(child.motion) + " at " + child.threshold + " x" + child.timeScale)) + ")";
            }

            return motion != null ? AssetDatabase.GetAssetPath(motion) + "#" + motion.name : "-";
        }

        private static void Describe(StringBuilder text, string from, AnimatorStateTransition transition)
        {
            text.Append("transition ").Append(from).Append(" -> ").Append(transition.destinationState != null ? transition.destinationState.name : "-")
                .Append(" exit ").Append(transition.hasExitTime).Append(' ').Append(transition.exitTime)
                .Append(" fade ").Append(transition.hasFixedDuration).Append(' ').Append(transition.duration)
                .Append(" self ").Append(transition.canTransitionToSelf)
                .Append(" if ").Append(string.Join(" and ", transition.conditions.Select(condition => condition.mode + " " + condition.parameter + " " + condition.threshold)))
                .Append('\n');
        }

        // Destroys a controller made only to compare, with every object of its own.
        private static void DestroyInMemory(AnimatorController controller)
        {
            var owned = new List<UnityEngine.Object>();
            foreach (var layer in controller.layers)
            {
                var machine = layer.stateMachine;
                owned.Add(machine);
                owned.AddRange(machine.anyStateTransitions);
                foreach (var state in machine.states.Select(child => child.state))
                {
                    owned.Add(state);
                    owned.AddRange(state.transitions);
                    if (state.motion is BlendTree tree)
                    {
                        owned.Add(tree);
                    }
                }
            }

            owned.Add(controller);
            foreach (var item in owned.Where(item => item != null))
            {
                UnityEngine.Object.DestroyImmediate(item);
            }
        }

        // The parameters, states, and transitions of a creature's controller from its model's clips;
        // a persistent controller keeps its blend tree as a sub-asset.
        private static void Fill(AnimatorController controller, string modelPath, float walkSpeed, float runSpeed, bool persistent)
        {
            controller.AddParameter(CreatureLocomotion.SpeedParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(CreatureLocomotion.MovingParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = CreatureLocomotion.RateParameter,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 1f,
            });
            controller.AddParameter(CreatureLocomotion.AttackParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(CreatureLocomotion.DeadParameter, AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState(CreatureLocomotion.IdleState, new Vector3(300f, 0f));
            idle.motion = Clip(modelPath, CreatureLocomotion.IdleClip);
            machine.defaultState = idle;

            var tree = new BlendTree
            {
                name = CreatureLocomotion.LocomotionState,
                blendType = BlendTreeType.Simple1D,
                blendParameter = CreatureLocomotion.SpeedParameter,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            if (persistent)
            {
                AssetDatabase.AddObjectToAsset(tree, controller);
            }

            tree.AddChild(Clip(modelPath, CreatureLocomotion.WalkClip), walkSpeed);
            tree.AddChild(Clip(modelPath, CreatureLocomotion.RunClip), runSpeed);
            var locomotion = machine.AddState(CreatureLocomotion.LocomotionState, new Vector3(300f, 100f));
            locomotion.motion = tree;
            locomotion.speedParameter = CreatureLocomotion.RateParameter;
            locomotion.speedParameterActive = true;

            var attack = machine.AddState(CreatureLocomotion.AttackState, new Vector3(600f, 50f));
            attack.motion = Clip(modelPath, CreatureLocomotion.AttackClip);
            var death = machine.AddState(CreatureLocomotion.DeathState, new Vector3(600f, 200f));
            death.motion = Clip(modelPath, CreatureLocomotion.DeathClip);

            var walk = idle.AddTransition(locomotion);
            Configure(walk, 0.15f);
            walk.AddCondition(AnimatorConditionMode.If, 0f, CreatureLocomotion.MovingParameter);
            var stop = locomotion.AddTransition(idle);
            Configure(stop, 0.2f);
            stop.AddCondition(AnimatorConditionMode.IfNot, 0f, CreatureLocomotion.MovingParameter);

            // An attack plays over whatever the creature does and goes back to it at its end.
            var bite = machine.AddAnyStateTransition(attack);
            Configure(bite, CreatureLocomotion.AttackCrossfadeSeconds);
            bite.canTransitionToSelf = false;
            bite.AddCondition(AnimatorConditionMode.If, 0f, CreatureLocomotion.AttackParameter);
            bite.AddCondition(AnimatorConditionMode.IfNot, 0f, CreatureLocomotion.DeadParameter);
            foreach (var (target, moving) in new[] { (locomotion, true), (idle, false) })
            {
                var back = attack.AddTransition(target);
                back.hasExitTime = true;
                back.exitTime = 0.85f;
                back.hasFixedDuration = true;
                back.duration = 0.15f;
                back.AddCondition(moving ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f,
                    CreatureLocomotion.MovingParameter);
            }

            // Death holds its last pose, the creature lying on the ground, until it lives again.
            var fall = machine.AddAnyStateTransition(death);
            Configure(fall, CreatureLocomotion.DeathCrossfadeSeconds);
            fall.canTransitionToSelf = false;
            fall.AddCondition(AnimatorConditionMode.If, 0f, CreatureLocomotion.DeadParameter);
            var rise = death.AddTransition(idle);
            Configure(rise, 0.2f);
            rise.AddCondition(AnimatorConditionMode.IfNot, 0f, CreatureLocomotion.DeadParameter);
        }

        private static void Configure(AnimatorStateTransition transition, float seconds)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = seconds;
        }

        private static Material BuildAlphaMaterial()
        {
            var wolf = ArtModelImportRules.FindProjectMaterial("M_Wolf");
            if (wolf == null)
            {
                throw new InvalidOperationException("The project material M_Wolf is missing; export the wolf first.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(FeralAlphaMaterial);
            if (material == null)
            {
                material = new Material(wolf) { name = "M_Mob_FeralAlpha" };
                AssetDatabase.CreateAsset(material, FeralAlphaMaterial);
            }

            material.shader = wolf.shader;
            material.CopyPropertiesFromMaterial(wolf);
            material.SetColor("_BaseColor", FeralAlphaTint);
            DevelopmentWorldPalette.ValidateAsTheInspectorDoes(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        // The prefab shows the model at its scale below the actor's root, which keeps its own scale
        // so the actor's interaction bounds stay in meters.
        public static void BuildPrefab(
            string prefabPath,
            string modelPath,
            RuntimeAnimatorController controller,
            float scale,
            Material material)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                if (Shows(root, modelPath, controller, scale, material))
                {
                    return;
                }

                foreach (var component in root.GetComponents<Component>()
                             .Where(component => component is MeshFilter || component is Renderer || component is Collider))
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }

                foreach (Transform child in root.transform.Cast<Transform>().ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }

                var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath), root.transform);
                model.name = ModelName;
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = Vector3.one * scale;
                var animator = model.GetComponent<Animator>();
                if (animator == null)
                {
                    animator = model.AddComponent<Animator>();
                }

                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                if (material != null)
                {
                    foreach (var renderer in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
                    }
                }

                var creature = root.GetComponent<CreatureAnimator>();
                if (creature == null)
                {
                    creature = root.AddComponent<CreatureAnimator>();
                }

                creature.Bind(animator, WolfWalkSpeed, WolfRunSpeed);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // Whether a prefab already shows the model as BuildPrefab makes it: its only child, at that
        // scale, with the controller, in the material or else the model's own, bound to its
        // creature, and no mesh or collider of the root's own.
        private static bool Shows(GameObject root, string modelPath, RuntimeAnimatorController controller, float scale, Material material)
        {
            if (root.transform.childCount != 1
                || root.GetComponents<Component>().Any(component => component is MeshFilter || component is Renderer || component is Collider))
            {
                return false;
            }

            var model = root.transform.GetChild(0);
            var animator = model.GetComponent<Animator>();
            var creature = root.GetComponent<CreatureAnimator>();
            return model.name == ModelName
                && PrefabUtility.GetCorrespondingObjectFromSource(model.gameObject) == AssetDatabase.LoadAssetAtPath<GameObject>(modelPath)
                && model.localPosition == Vector3.zero
                && model.localRotation == Quaternion.identity
                && model.localScale == Vector3.one * scale
                && animator != null
                && animator.runtimeAnimatorController == controller
                && !animator.applyRootMotion
                && animator.cullingMode == AnimatorCullingMode.CullUpdateTransforms
                && model.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(renderer => renderer.sharedMaterials.SequenceEqual(
                    material != null
                        ? Enumerable.Repeat(material, renderer.sharedMaterials.Length)
                        : PrefabUtility.GetCorrespondingObjectFromSource(renderer).sharedMaterials))
                && creature != null
                && creature.Animator == animator
                && creature.WalkSpeed == WolfWalkSpeed
                && creature.RunSpeed == WolfRunSpeed;
        }
    }
}
