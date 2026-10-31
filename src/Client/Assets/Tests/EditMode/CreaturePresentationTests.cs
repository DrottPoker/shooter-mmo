using System.Linq;
using Mmorpg.Client.Editor;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.WorldActors;
using Mmorpg.WorldData.Actors;
using Mmorpg.WorldData.Editor.Actors;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Mmorpg.Client.Tests.EditMode
{
    // The creatures with clips of their own, as CreaturePresentationBuilder builds them: the wolf's
    // Animator controller, the Feral Wolf and the larger, darker Feral Alpha, how the clips follow
    // the ground speed, and the Mob corpses that show the wolf lying dead. See
    // docs/ART_PIPELINE.md and docs/UNITY_CLIENT_ARCHITECTURE.md.
    public sealed class CreaturePresentationTests
    {
        private GameObject instance;

        [TearDown]
        public void DestroyInstance()
        {
            if (instance != null)
            {
                Object.DestroyImmediate(instance);
            }
        }

        // A build that changes nothing rewrites nothing: the controller, the prefabs, and the
        // Feral Alpha's material stay exactly as they are on disk.
        [Test]
        public void BuildingTheCreaturesAgainRewritesNothing()
        {
            var paths = new[]
            {
                CreaturePresentationBuilder.WolfController,
                CreaturePresentationBuilder.FeralWolfPrefab,
                CreaturePresentationBuilder.FeralAlphaPrefab,
                CreaturePresentationBuilder.FeralAlphaMaterial,
            };
            var before = paths.Select(path => System.IO.File.ReadAllText(path) + System.IO.File.ReadAllText(path + ".meta")).ToArray();

            CreaturePresentationBuilder.Build();

            for (var index = 0; index < paths.Length; index++)
            {
                var after = System.IO.File.ReadAllText(paths[index]) + System.IO.File.ReadAllText(paths[index] + ".meta");
                Assert.That(after, Is.EqualTo(before[index]), paths[index] + " was rewritten");
            }
        }

        [Test]
        public void TheWolfControllerBlendsWalkAndRunBySpeedAndStandsInIdle()
        {
            var controller = Controller();
            Assert.That(
                controller.parameters.Select(parameter => (parameter.name, parameter.type)),
                Is.EquivalentTo(new[]
                {
                    (CreatureLocomotion.SpeedParameter, AnimatorControllerParameterType.Float),
                    (CreatureLocomotion.MovingParameter, AnimatorControllerParameterType.Bool),
                    (CreatureLocomotion.RateParameter, AnimatorControllerParameterType.Float),
                    (CreatureLocomotion.AttackParameter, AnimatorControllerParameterType.Trigger),
                    (CreatureLocomotion.DeadParameter, AnimatorControllerParameterType.Bool),
                }));
            Assert.That(
                controller.parameters.Single(parameter => parameter.name == CreatureLocomotion.RateParameter).defaultFloat,
                Is.EqualTo(1f));
            Assert.That(controller.layers, Has.Length.EqualTo(1));

            var machine = controller.layers[0].stateMachine;
            var states = States(machine);
            Assert.That(
                states.Keys,
                Is.EquivalentTo(new[]
                {
                    CreatureLocomotion.IdleState, CreatureLocomotion.LocomotionState,
                    CreatureLocomotion.AttackState, CreatureLocomotion.DeathState,
                }));
            var idle = states[CreatureLocomotion.IdleState];
            var locomotion = states[CreatureLocomotion.LocomotionState];
            Assert.That(machine.defaultState, Is.SameAs(idle));
            Assert.That(Clip(idle.motion, CreatureLocomotion.IdleClip).isLooping, Is.True);

            Assert.That(locomotion.speedParameterActive, Is.True);
            Assert.That(locomotion.speedParameter, Is.EqualTo(CreatureLocomotion.RateParameter));
            var tree = locomotion.motion as BlendTree;
            Assert.That(tree, Is.Not.Null);
            Assert.That(tree.blendType, Is.EqualTo(BlendTreeType.Simple1D));
            Assert.That(tree.blendParameter, Is.EqualTo(CreatureLocomotion.SpeedParameter));
            Assert.That(tree.useAutomaticThresholds, Is.False);
            Assert.That(tree.children, Has.Length.EqualTo(2));
            Assert.That(Clip(tree.children[0].motion, CreatureLocomotion.WalkClip).isLooping, Is.True);
            Assert.That(tree.children[0].threshold, Is.EqualTo(CreaturePresentationBuilder.WolfWalkSpeed));
            Assert.That(Clip(tree.children[1].motion, CreatureLocomotion.RunClip).isLooping, Is.True);
            Assert.That(tree.children[1].threshold, Is.EqualTo(CreaturePresentationBuilder.WolfRunSpeed));

            AssertTransition(idle, locomotion, AnimatorConditionMode.If, CreatureLocomotion.MovingParameter);
            AssertTransition(locomotion, idle, AnimatorConditionMode.IfNot, CreatureLocomotion.MovingParameter);
        }

        [Test]
        public void TheWolfBitesOverWhatItDoesAndFallsIntoDeathWhileDead()
        {
            var machine = Controller().layers[0].stateMachine;
            var states = States(machine);
            var attack = states[CreatureLocomotion.AttackState];
            var death = states[CreatureLocomotion.DeathState];
            Assert.That(Clip(attack.motion, CreatureLocomotion.AttackClip).isLooping, Is.False);
            // Death holds its last pose, lying on the ground.
            Assert.That(Clip(death.motion, CreatureLocomotion.DeathClip).isLooping, Is.False);
            Assert.That(machine.anyStateTransitions, Has.Length.EqualTo(2));

            var bite = machine.anyStateTransitions.Single(transition => transition.destinationState == attack);
            Assert.That(bite.canTransitionToSelf, Is.False);
            Assert.That(bite.hasExitTime, Is.False);
            Assert.That(bite.duration, Is.EqualTo(CreatureLocomotion.AttackCrossfadeSeconds));
            Assert.That(
                bite.conditions.Select(condition => (condition.mode, condition.parameter)),
                Is.EquivalentTo(new[]
                {
                    (AnimatorConditionMode.If, CreatureLocomotion.AttackParameter),
                    (AnimatorConditionMode.IfNot, CreatureLocomotion.DeadParameter),
                }));

            // At its end the bite goes back to what the creature does.
            Assert.That(
                attack.transitions.Select(transition => (transition.destinationState.name, transition.hasExitTime, transition.conditions.Single().mode)),
                Is.EquivalentTo(new[]
                {
                    (CreatureLocomotion.LocomotionState, true, AnimatorConditionMode.If),
                    (CreatureLocomotion.IdleState, true, AnimatorConditionMode.IfNot),
                }));

            var fall = machine.anyStateTransitions.Single(transition => transition.destinationState == death);
            Assert.That(fall.canTransitionToSelf, Is.False);
            Assert.That(fall.hasExitTime, Is.False);
            Assert.That(fall.duration, Is.EqualTo(CreatureLocomotion.DeathCrossfadeSeconds));
            Assert.That(fall.conditions.Single().mode, Is.EqualTo(AnimatorConditionMode.If));
            Assert.That(fall.conditions.Single().parameter, Is.EqualTo(CreatureLocomotion.DeadParameter));
            AssertTransition(death, states[CreatureLocomotion.IdleState], AnimatorConditionMode.IfNot, CreatureLocomotion.DeadParameter);
        }

        [TestCase(CreaturePresentationBuilder.FeralWolfPrefab, 1f, "M_Wolf")]
        [TestCase(CreaturePresentationBuilder.FeralAlphaPrefab, CreaturePresentationBuilder.FeralAlphaScale, "M_Mob_FeralAlpha")]
        public void TheWolvesShowTheWolfModelWithItsAnimator(string prefabPath, float scale, string material)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            // The root keeps the actor's own scale, so its interaction bounds stay in meters, and
            // the presentation adds its colliders at run time.
            Assert.That(prefab.transform.localScale, Is.EqualTo(Vector3.one));
            Assert.That(prefab.GetComponent<Renderer>(), Is.Null);
            Assert.That(prefab.GetComponentsInChildren<Collider>(true), Is.Empty);

            var model = prefab.transform.Find(CreaturePresentationBuilder.ModelName);
            Assert.That(model, Is.Not.Null);
            Assert.That(
                AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(model.gameObject)),
                Is.EqualTo(CreaturePresentationBuilder.WolfModel));
            Assert.That(model.localScale, Is.EqualTo(Vector3.one * scale));

            var animator = model.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(animator.runtimeAnimatorController), Is.EqualTo(CreaturePresentationBuilder.WolfController));
            Assert.That(animator.avatar, Is.Not.Null);
            Assert.That(animator.avatar.isValid, Is.True);
            Assert.That(animator.avatar.isHuman, Is.False);
            // The game moves the Mob; the clips play in place.
            Assert.That(animator.applyRootMotion, Is.False);
            Assert.That(animator.cullingMode, Is.EqualTo(AnimatorCullingMode.CullUpdateTransforms));

            var creature = prefab.GetComponent<CreatureAnimator>();
            Assert.That(creature, Is.Not.Null);
            Assert.That(creature.Animator, Is.SameAs(animator));
            Assert.That(creature.WalkSpeed, Is.EqualTo(CreaturePresentationBuilder.WolfWalkSpeed));
            Assert.That(creature.RunSpeed, Is.EqualTo(CreaturePresentationBuilder.WolfRunSpeed));

            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers, Has.Length.EqualTo(1));
            Assert.That(renderers[0], Is.InstanceOf<SkinnedMeshRenderer>());
            Assert.That(renderers[0].sharedMaterials.Select(value => value.name), Is.All.EqualTo(material));
        }

        [Test]
        public void TheFeralAlphaWearsTheWolfsCoatDarker()
        {
            var wolf = ArtModelImportRules.FindProjectMaterial("M_Wolf");
            var alpha = AssetDatabase.LoadAssetAtPath<Material>(CreaturePresentationBuilder.FeralAlphaMaterial);
            Assert.That(wolf, Is.Not.Null);
            Assert.That(alpha, Is.Not.Null);
            Assert.That(alpha.shader, Is.SameAs(wolf.shader));
            Assert.That(alpha.GetTexture("_BaseMap"), Is.Not.Null);
            Assert.That(alpha.GetTexture("_BaseMap"), Is.SameAs(wolf.GetTexture("_BaseMap")));
            var tint = alpha.GetColor("_BaseColor");
            var coat = wolf.GetColor("_BaseColor");
            Assert.That(tint.r, Is.LessThan(coat.r));
            Assert.That(tint.g, Is.LessThan(coat.g));
            Assert.That(tint.b, Is.LessThan(coat.b));
            // The Feral Wolf shows the wolf's own material.
            Assert.That(
                AssetDatabase.LoadAssetAtPath<Material>(CreaturePresentationBuilder.FeralWolfPlaceholderMaterial),
                Is.Null);
        }

        // The wolf walks its patrol at the speed its Walk clip was made for and chases at the speed
        // of its Run clip, so its paws stay planted.
        [TestCase("mob.feral_wolf")]
        [TestCase("mob.feral_alpha")]
        public void TheWolfClipsAreMadeForThePatrolAndChaseSpeeds(string actorId)
        {
            var catalog = new WorldActorEditorService(WorldActorEditorPaths.CreateDefault()).Load().Catalog;
            var actor = catalog.actors.Single(entry => entry.id == actorId);
            var profile = catalog.locomotionProfiles.Single(entry => entry.id == actor.locomotionProfileId);
            Assert.That(profile.patrolSpeed, Is.EqualTo(CreaturePresentationBuilder.WolfWalkSpeed));
            Assert.That(profile.walkSpeed, Is.EqualTo(CreaturePresentationBuilder.WolfRunSpeed));
        }

        [Test]
        public void TheClipsPlayAtTheRateThatKeepsThePawsPlanted()
        {
            const float Walk = 1.4f;
            const float Run = 4.5f;
            const float WalkCycle = 0.6f;
            const float RunCycle = 0.4f;

            CreatureLocomotion.Evaluate(Walk, 1f, Walk, Run, WalkCycle, RunCycle, out var blend, out var rate);
            Assert.That(blend, Is.EqualTo(Walk));
            Assert.That(rate, Is.EqualTo(1f).Within(1e-5f));
            CreatureLocomotion.Evaluate(Run, 1f, Walk, Run, WalkCycle, RunCycle, out blend, out rate);
            Assert.That(blend, Is.EqualTo(Run));
            Assert.That(rate, Is.EqualTo(1f).Within(1e-5f));

            // Slower than the walk the walk slows down, but never below the minimum rate.
            CreatureLocomotion.Evaluate(0.7f, 1f, Walk, Run, WalkCycle, RunCycle, out blend, out rate);
            Assert.That(blend, Is.EqualTo(Walk));
            Assert.That(rate, Is.EqualTo(0.5f).Within(1e-5f));
            CreatureLocomotion.Evaluate(0f, 1f, Walk, Run, WalkCycle, RunCycle, out _, out rate);
            Assert.That(rate, Is.EqualTo(CreatureLocomotion.MinimumRate));

            // The Feral Alpha's longer strides cover the same ground in fewer steps.
            CreatureLocomotion.Evaluate(Walk, 1.3f, Walk, Run, WalkCycle, RunCycle, out blend, out rate);
            Assert.That(blend, Is.EqualTo(Walk));
            Assert.That(rate, Is.EqualTo(1f / 1.3f).Within(1e-5f));
            CreatureLocomotion.Evaluate(Run * 1.3f, 1.3f, Walk, Run, WalkCycle, RunCycle, out blend, out rate);
            Assert.That(blend, Is.EqualTo(Run));
            Assert.That(rate, Is.EqualTo(1f).Within(1e-5f));

            // Between the gaits the blended stride covers the ground the creature moves.
            const float Speed = 3f;
            CreatureLocomotion.Evaluate(Speed, 1f, Walk, Run, WalkCycle, RunCycle, out blend, out rate);
            var weight = (blend - Walk) / (Run - Walk);
            var cycle = Mathf.Lerp(WalkCycle, RunCycle, weight);
            var stride = Mathf.Lerp(Walk * WalkCycle, Run * RunCycle, weight);
            Assert.That(weight, Is.InRange(0f, 1f));
            Assert.That(stride / (cycle / rate), Is.EqualTo(Speed).Within(1e-4f));
        }

        [Test]
        public void TheFeralAlphaStandsTheScaleOfTheFeralWolfTaller()
        {
            var wolf = StandingHeight(CreaturePresentationBuilder.FeralWolfPrefab);
            var alpha = StandingHeight(CreaturePresentationBuilder.FeralAlphaPrefab);
            Assert.That(wolf, Is.InRange(0.5f, 1.4f));
            Assert.That(alpha / wolf, Is.EqualTo(CreaturePresentationBuilder.FeralAlphaScale).Within(0.01f));
        }

        [Test]
        public void AWolfCorpseLiesInTheLastPoseOfDeath()
        {
            var standing = StandingHeight(CreaturePresentationBuilder.FeralWolfPrefab);
            var creature = instance.GetComponent<CreatureAnimator>();
            creature.LieDead(-1f);

            Assert.That(creature.IsDead, Is.True);
            var state = creature.Animator.GetCurrentAnimatorStateInfo(0);
            Assert.That(state.shortNameHash, Is.EqualTo(Animator.StringToHash(CreatureLocomotion.DeathState)));
            Assert.That(state.normalizedTime, Is.GreaterThanOrEqualTo(0.99f));
            // The wolf lies on its side, so it is far lower than it stands.
            Assert.That(BakedHeight(instance), Is.LessThan(standing * 0.6f));
            // A dead wolf does not bite.
            creature.PlayAttack();
            Assert.That(creature.Animator.GetBool(CreatureLocomotion.AttackParameter), Is.False);
        }

        [TestCase("corpse.mob.feral_wolf", "mob.feral_wolf")]
        [TestCase("corpse.mob.feral_alpha", "mob.feral_alpha")]
        [TestCase("corpse.mob", null)]
        [TestCase("corpse.mob.", null)]
        [TestCase("corpse.player", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void AMobCorpseShowsTheModelOfItsArchetype(string presentationKey, string archetype)
        {
            Assert.That(CorpsePresentationController.MobArchetypeOf(presentationKey), Is.EqualTo(archetype));
            if (archetype != null)
            {
                Assert.That(WorldActorCorpseRules.MobCorpsePresentationKeyFor(archetype), Is.EqualTo(presentationKey));
            }
        }

        // A corpse that arrives before the presentation registry has loaded waits for its model
        // instead of staying a capsule.
        [Test]
        public void AMobModelCountsAsLoadingUntilThePresentationRegistryLoads()
        {
            instance = new GameObject("WorldActorPresentations");
            var presentations = instance.AddComponent<WorldActorPresentationController>();

            Assert.That(presentations.TryGetArchetypePrefab("mob.feral_wolf", out var prefab, out var loading), Is.False);
            Assert.That(prefab, Is.Null);
            Assert.That(loading, Is.True);
        }

        private static AnimatorController Controller()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(CreaturePresentationBuilder.WolfController);
            Assert.That(controller, Is.Not.Null);
            return controller;
        }

        private static System.Collections.Generic.Dictionary<string, AnimatorState> States(AnimatorStateMachine machine)
        {
            return machine.states.Select(child => child.state).ToDictionary(state => state.name);
        }

        private static AnimationClip Clip(Motion motion, string name)
        {
            var clip = motion as AnimationClip;
            Assert.That(clip, Is.Not.Null, name);
            Assert.That(clip.name, Is.EqualTo(name));
            Assert.That(AssetDatabase.GetAssetPath(clip), Is.EqualTo(CreaturePresentationBuilder.WolfModel), name);
            Assert.That(clip.isHumanMotion, Is.False, name);
            return clip;
        }

        private static void AssertTransition(AnimatorState from, AnimatorState to, AnimatorConditionMode mode, string parameter)
        {
            var transition = from.transitions.Single(candidate => candidate.destinationState == to);
            Assert.That(transition.hasExitTime, Is.False, from.name + " to " + to.name);
            Assert.That(transition.hasFixedDuration, Is.True);
            var condition = transition.conditions.Single();
            Assert.That(condition.mode, Is.EqualTo(mode));
            Assert.That(condition.parameter, Is.EqualTo(parameter));
        }

        // The height of the creature of the prefab standing in Idle, which stays in instance.
        private float StandingHeight(string prefabPath)
        {
            DestroyInstance();
            instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var animator = instance.GetComponent<CreatureAnimator>().Animator;
            animator.Rebind();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Update(0f);
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            return BakedHeight(instance);
        }

        private static float BakedHeight(GameObject creature)
        {
            var skin = creature.GetComponentInChildren<SkinnedMeshRenderer>();
            var mesh = new Mesh();
            try
            {
                skin.BakeMesh(mesh, true);
                var heights = mesh.vertices.Select(vertex => skin.transform.TransformPoint(vertex).y).ToArray();
                return heights.Max() - heights.Min();
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
