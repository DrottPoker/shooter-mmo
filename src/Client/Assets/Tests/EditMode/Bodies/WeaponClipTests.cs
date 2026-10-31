using System.Collections.Generic;
using System.Linq;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Items;
using Mmorpg.WorldData.Items.Presentation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Mmorpg.Client.Tests.EditMode
{
    // A weapon with clips, such as the Oak Staff, is held in its pose on its hand's arm, plays its
    // attack clip over the body timed to strike when its attack releases, and launches its
    // projectile from its launch point; a bow draws its string with the drawing hand, and the other
    // hand takes a greataxe as its swing brings it to the haft, as docs/EQUIPMENT_VISUALS_DESIGN.md
    // describes under Weapon Poses And Attack Clips.
    public sealed class WeaponClipTests
    {
        private const string ControllerPath = "Assets/Art/Animation/HumanLocomotion.controller";

        private static readonly string[] BodyPrefabPaths =
        {
            "Assets/Prefabs/Player/HumanMaleBody.prefab",
            "Assets/Prefabs/Player/HumanFemaleBody.prefab",
        };

        private ClientConfig config;
        private AsyncOperationHandle<ClientConfig> configHandle;
        private ClientItemCatalog bundled;

        [SetUp]
        public void SetUp()
        {
            ItemPresentationCatalogLoader.ResetCache();
            config = ClientConfig.Load(out configHandle);
            Assert.That(ClientItemCatalog.TryLoad(config.ItemGameplayCatalog, out bundled, out var error), Is.True, error);
        }

        [TearDown]
        public void TearDown()
        {
            ItemPresentationCatalogLoader.ResetCache();
            ClientAssets.Release(configHandle);
            configHandle = default;
        }

        [Test]
        public void TheOakStaffNamesItsPoseItsCastAndItsCrystal()
        {
            Assert.That(bundled.Presentation.TryGet("weapon.oak_staff", out var entry), Is.True);
            Assert.That(entry.attachment, Is.EqualTo("OakStaff"));
            foreach (var catalog in Catalogs())
            {
                var staff = catalog.AttachmentNamed("OakStaff");
                Assert.That(staff, Is.Not.Null, catalog.name);
                Assert.That(staff.IsTool, Is.False);
                Assert.That(staff.Bone, Is.EqualTo(HumanLocomotion.MainHandSocketBone));
                Assert.That(staff.HoldClip, Is.EqualTo(HumanLocomotion.HoldStaffClip));
                Assert.That(staff.AttackClip, Is.EqualTo(HumanLocomotion.CastClip));
                Assert.That(staff.HasLaunchPoint, Is.True);
                // The bolt leaves from the crystal, which lies on the model 70 cm along the shaft.
                var nearest = ModelVertices(staff.Model).Min(vertex => (vertex - staff.LaunchPoint).magnitude);
                Assert.That(nearest, Is.LessThan(0.05f), "the launch point lies off the staff");
                Assert.That(staff.LaunchPoint.magnitude, Is.EqualTo(0.7f).Within(0.01f));
            }
        }

        // The sword slashes, the greataxe is held in the right hand and swung with both, and the bow
        // is held in the left hand, with its own bones and its clip Draw, and shoots from the arrow
        // pass on its riser.
        [Test]
        public void TheSwordTheGreataxeAndTheBowNameTheirClips()
        {
            foreach (var catalog in Catalogs())
            {
                var sword = catalog.AttachmentNamed("IronSword");
                Assert.That(sword.Bone, Is.EqualTo(HumanLocomotion.MainHandSocketBone), catalog.name);
                Assert.That(sword.HoldClip, Is.Empty);
                Assert.That(sword.AttackClip, Is.EqualTo("SwordSlash"));
                Assert.That(sword.HoldsWithBothHands, Is.False);

                var greataxe = catalog.AttachmentNamed("IronGreataxe");
                Assert.That(greataxe.Bone, Is.EqualTo(HumanLocomotion.MainHandSocketBone), catalog.name);
                Assert.That(greataxe.HoldClip, Is.EqualTo("HoldGreataxe"));
                Assert.That(greataxe.AttackClip, Is.EqualTo("GreataxeSwing"));
                Assert.That(greataxe.HoldsWithBothHands, Is.True);
                Assert.That(SurfaceDistance(greataxe.Model, greataxe.OffHandGrip), Is.LessThan(0.03f), "the other hand's grip lies off the haft");

                var bow = catalog.AttachmentNamed("TrainingShortbow");
                Assert.That(bow.Socket, Is.EqualTo("off_hand"), catalog.name);
                Assert.That(bow.Bone, Is.EqualTo(HumanLocomotion.OffHandSocketBone));
                Assert.That(bow.HoldClip, Is.EqualTo("HoldBow"));
                Assert.That(bow.AttackClip, Is.EqualTo("BowShot"));
                Assert.That(bow.IsSkinned, Is.True);
                Assert.That(bow.Skeleton, Is.EqualTo("Bow"));
                Assert.That(bow.ClipNamed(BowRig.DrawClip), Is.Not.Null);
                Assert.That(bow.DrawLength, Is.EqualTo(0.62f).Within(0.001f));
                Assert.That(bow.HasLaunchPoint, Is.True);
                var pass = ModelVertices(bow.Model).Min(vertex => (vertex - bow.LaunchPoint).magnitude);
                Assert.That(pass, Is.LessThan(0.05f), "the arrow pass lies off the bow");
            }
        }

        // The server releases an attack when its weapon family's windup ends, so each attack clip
        // releases as far in, and the clip plays from its start.
        [Test]
        public void EveryAttackClipReleasesWhenItsFamilysWindupEnds()
        {
            var families = bundled.Gameplay.WeaponFamilies.ToDictionary(family => family.Id);
            var clips = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath).animationClips
                .GroupBy(clip => clip.name)
                .ToDictionary(group => group.Key, group => group.First());
            var checkedWeapons = new List<string>();
            foreach (var definition in bundled.Definitions.Where(definition => definition.Weapon != null))
            {
                Assert.That(bundled.Presentation.TryGet(definition.Id, out var entry), Is.True, definition.Id);
                var windup = families[definition.Weapon.WeaponFamily].WindupSeconds;
                foreach (var catalog in Catalogs())
                {
                    var attachment = catalog.AttachmentNamed(entry.attachment);
                    if (attachment == null || attachment.AttackClip.Length == 0)
                    {
                        continue;
                    }

                    Assert.That(attachment.AttackRelease, Is.EqualTo(windup).Within(0.001f), definition.Id);
                    Assert.That(attachment.AttackRelease, Is.LessThan(clips[attachment.AttackClip].length), definition.Id);
                    checkedWeapons.Add(definition.Id);
                }
            }

            Assert.That(checkedWeapons.Distinct(), Is.EquivalentTo(new[]
            {
                "weapon.iron_greataxe", "weapon.iron_sword", "weapon.oak_staff", "weapon.training_shortbow",
            }));
            Assert.That(HumanLocomotion.OneHandStrikeReleaseSeconds, Is.LessThan(clips[HumanLocomotion.OneHandStrikeClip].length));
        }

        [Test]
        public void EveryWeaponClipHasAStateOnItsLayerWithItsMask()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            var layers = controller.layers;
            var mainHold = layers.SingleOrDefault(layer => layer.name == HumanLocomotion.MainHandHoldLayer);
            var offHold = layers.SingleOrDefault(layer => layer.name == HumanLocomotion.OffHandHoldLayer);
            var action = layers.SingleOrDefault(layer => layer.name == HumanLocomotion.ActionLayer);
            Assert.That(mainHold, Is.Not.Null);
            Assert.That(offHold, Is.Not.Null);
            Assert.That(action, Is.Not.Null);
            // The attack plays over the hold poses, which play over the locomotion clips.
            Assert.That(System.Array.IndexOf(layers, action), Is.GreaterThan(System.Array.IndexOf(layers, mainHold)));
            Assert.That(System.Array.IndexOf(layers, action), Is.GreaterThan(System.Array.IndexOf(layers, offHold)));
            AssertMask(mainHold.avatarMask, AvatarMaskBodyPart.RightArm);
            AssertMask(offHold.avatarMask, AvatarMaskBodyPart.LeftArm);
            // An attack moves the whole body, the feet too, such as a step into a cut, and the root
            // carries the turn and shift of its hips, such as an archer's stance.
            AssertMask(
                action.avatarMask,
                AvatarMaskBodyPart.Root,
                AvatarMaskBodyPart.Body,
                AvatarMaskBodyPart.Head,
                AvatarMaskBodyPart.LeftLeg,
                AvatarMaskBodyPart.RightLeg,
                AvatarMaskBodyPart.LeftArm,
                AvatarMaskBodyPart.RightArm);
            foreach (var layer in new[] { mainHold, offHold, action })
            {
                Assert.That(layer.defaultWeight, Is.Zero, layer.name);
                Assert.That(layer.blendingMode, Is.EqualTo(AnimatorLayerBlendingMode.Override), layer.name);
                Assert.That(layer.stateMachine.defaultState.name, Is.EqualTo(HumanLocomotion.EmptyState), layer.name);
                Assert.That(layer.stateMachine.defaultState.motion, Is.Null, layer.name);
            }

            var weapons = Catalogs().SelectMany(catalog => catalog.Attachments)
                .Where(attachment => attachment.HoldClip.Length > 0 || attachment.AttackClip.Length > 0)
                .ToArray();
            Assert.That(weapons.Select(weapon => weapon.Name).Distinct(),
                Is.EquivalentTo(new[] { "IronGreataxe", "IronSword", "OakStaff", "TrainingShortbow" }));
            foreach (var weapon in weapons)
            {
                // The hold pose loops on its hand's arm; the attack plays once and the body fades it out.
                if (weapon.HoldClip.Length > 0)
                {
                    AssertClipState(weapon.Bone == HumanLocomotion.OffHandSocketBone ? offHold : mainHold, weapon.HoldClip, true);
                }

                AssertClipState(action, weapon.AttackClip, false);
            }

            // A one-handed melee weapon without a clip of its own strikes with OneHandStrike.
            AssertClipState(action, HumanLocomotion.OneHandStrikeClip, false);
        }

        [Test]
        public void AnAttackClipFadesInPlaysOnceAndFadesOut()
        {
            const float length = 0.9f;
            Assert.That(HumanLocomotion.ActionWeight(-0.01f, length), Is.Zero);
            Assert.That(HumanLocomotion.ActionWeight(0f, length), Is.Zero);
            Assert.That(HumanLocomotion.ActionWeight(HumanLocomotion.ActionFadeInSeconds * 0.5f, length), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(HumanLocomotion.ActionWeight(0.4f, length), Is.EqualTo(1f));
            Assert.That(
                HumanLocomotion.ActionWeight(length - (HumanLocomotion.ActionFadeOutSeconds * 0.5f), length),
                Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(HumanLocomotion.ActionWeight(length, length), Is.Zero);

            // A clip that began late fades in from where it began to show.
            Assert.That(HumanLocomotion.ActionWeight(0.25f, length, 0.3f), Is.Zero);
            Assert.That(HumanLocomotion.ActionWeight(0.3f, length, 0.3f), Is.Zero);
            Assert.That(
                HumanLocomotion.ActionWeight(0.3f + (HumanLocomotion.ActionFadeInSeconds * 0.5f), length, 0.3f),
                Is.EqualTo(0.5f).Within(0.001f));
        }

        // An attack clip starts so its release comes when the attack releases: from its start when
        // the attack releases after the clip's own windup, later for a longer windup, and further
        // in for a shorter one, but never past its release.
        [Test]
        public void AnAttackClipStartsSoItStrikesWhenItsAttackReleases()
        {
            Assert.That(HumanLocomotion.AttackClipTime(0.8f, 0.8f), Is.Zero);
            Assert.That(HumanLocomotion.AttackClipTime(0.8f, 1.1f), Is.EqualTo(-0.3f).Within(0.0001f));
            Assert.That(HumanLocomotion.AttackClipTime(0.8f, 0.5f), Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(HumanLocomotion.AttackClipTime(0.8f, 0f), Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(HumanLocomotion.AttackClipTime(0.8f, -0.2f), Is.EqualTo(0.8f).Within(0.0001f));
        }

        // On both bodies, retargeted from the male rig, the staff stands upright beside the body in
        // the hold pose with its foot above the ground, and the thrust of Cast tips its crystal
        // forward toward the target.
        [Test]
        public void ABodyHoldsTheStaffUprightAndThrustsItForward()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    foreach (var path in BodyPrefabPaths)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        body.transform.SetPositionAndRotation(new Vector3(1f, 0f, 2f), Quaternion.Euler(0f, 30f, 0f));
                        var appearance = body.GetComponent<PlayerBodyAppearance>();
                        appearance.CombineParts(meshes);
                        appearance.ApplyLook(BodyLook.Default.WithAttachment("OakStaff", true), materials);
                        Assert.That(appearance.Weapon, Is.Not.Null, path);
                        Assert.That(appearance.Weapon.Name, Is.EqualTo("OakStaff"));
                        Assert.That(body.GetComponent<CharacterLocomotionAnimator>().HoldClip, Is.EqualTo(HumanLocomotion.HoldStaffClip));
                        var animator = body.GetComponent<Animator>();
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var holdLayer = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
                        var actionLayer = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
                        var socket = body.GetComponentsInChildren<Transform>().Single(bone => bone.name == HumanLocomotion.MainHandSocketBone);

                        animator.Play(HumanLocomotion.IdleState, 0, 0f);
                        animator.Play(HumanLocomotion.HoldStaffClip, holdLayer, 0f);
                        animator.SetLayerWeight(holdLayer, 1f);
                        animator.Update(0f);
                        Assert.That(appearance.TryGetLaunchPoint(out var crystal), Is.True, path);
                        var shaft = crystal - socket.position;
                        Assert.That(Vector3.Angle(shaft, Vector3.up), Is.LessThan(15f), path + " holds the staff upright");
                        var foot = socket.position - (shaft.normalized * 0.92f);
                        Assert.That(foot.y, Is.InRange(0.0f, 0.3f), path + " holds the staff's foot just above the ground");
                        var side = body.transform.InverseTransformPoint(socket.position).x;
                        Assert.That(side, Is.GreaterThan(0.15f), path + " holds the staff at its right side");

                        // The thrust, Cast's sixth frame of 27, over the hold pose.
                        animator.Play(HumanLocomotion.CastClip, actionLayer, 6f / 27f);
                        animator.SetLayerWeight(actionLayer, 1f);
                        animator.Update(0f);
                        Assert.That(appearance.TryGetLaunchPoint(out var thrust), Is.True, path);
                        var tilt = Vector3.Angle(thrust - socket.position, Vector3.up);
                        Assert.That(tilt, Is.InRange(15f, 60f), path + " tips the staff forward");
                        var ahead = body.transform.InverseTransformPoint(thrust).z;
                        Assert.That(ahead, Is.GreaterThan(0.5f), path + " points the crystal at the target");
                        TestContext.WriteLine(path + ": the staff leans " + Vector3.Angle(shaft, Vector3.up).ToString("F1")
                            + " degrees at rest and " + tilt.ToString("F1") + " in the thrust, its crystal " + ahead.ToString("F2") + " m ahead");
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // On both bodies the bow is held in the left hand; through BowShot the drawing hand takes the
        // string where it rests, draws it back along the arrow's line with the limbs bending, aims
        // the arrow at the target ahead in the archer's turned stance, and lets the string spring back
        // when the bow looses.
        [Test]
        public void ABodyDrawsTheBowsStringAlongTheArrowsLineAndLoosesIt()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    foreach (var path in BodyPrefabPaths)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        body.transform.SetPositionAndRotation(new Vector3(-1f, 0f, 3f), Quaternion.Euler(0f, -50f, 0f));
                        var appearance = body.GetComponent<PlayerBodyAppearance>();
                        appearance.CombineParts(meshes);
                        appearance.ApplyLook(BodyLook.Default.WithAttachment("TrainingShortbow", true), materials);
                        Assert.That(appearance.Weapon, Is.Not.Null, path);
                        Assert.That(appearance.Weapon.Name, Is.EqualTo("TrainingShortbow"));
                        Assert.That(appearance.Bow, Is.Not.Null, path + " draws no string");
                        var locomotion = body.GetComponent<CharacterLocomotionAnimator>();
                        Assert.That(locomotion.OffHandHoldClip, Is.EqualTo("HoldBow"));
                        Assert.That(locomotion.HoldClip, Is.Empty);

                        var animator = body.GetComponent<Animator>();
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var arm = BowRig.DrawingArm.Find(animator, body.transform);
                        Assert.That(arm, Is.Not.Null, path);
                        var bow = appearance.Bow;
                        Assert.That(Pose(animator, 2), Is.True);
                        bow.Update(true, arm, 0f);
                        Assert.That(bow.IsHeld, Is.False, path + " holds the string before its hand comes to it");

                        // Set, drawn to full draw: the nock in the hand, the limbs bent.
                        Pose(animator, 8);
                        bow.Update(true, arm, 0f);
                        Assert.That(bow.IsHeld, Is.True, path + " takes the string where it rests");
                        Pose(animator, 20);
                        bow.Update(true, arm, 0f);
                        Assert.That(bow.IsHeld, Is.True, path);
                        Assert.That(bow.Draw, Is.GreaterThan(0.7f), path + " draws the bow far");
                        Assert.That(appearance.TryGetNockedArrow(out var nock, out var forward, out var up), Is.True, path);
                        Assert.That((nock - arm.Socket.position).magnitude, Is.LessThan(0.001f), path + " keeps the nock in the hand");
                        Assert.That(Vector3.Angle(forward, body.transform.forward), Is.LessThan(10f), path + " aims the arrow ahead");
                        Assert.That(Vector3.Dot(up, forward), Is.EqualTo(0f).Within(0.001f));
                        Assert.That(body.transform.InverseTransformPoint(nock).y, Is.InRange(1.3f, 1.6f), path + " draws to the face");
                        TestContext.WriteLine(path + ": drawn to " + bow.Draw.ToString("F2") + ", the arrow "
                            + Vector3.Angle(forward, body.transform.forward).ToString("F1") + " degrees off ahead");

                        // Loosed, the string rests again until the next shot.
                        bow.Loose();
                        bow.Update(true, arm, 0f);
                        Assert.That(bow.IsHeld, Is.False, path);
                        Assert.That(bow.HasLoosed, Is.True, path);
                        Assert.That(bow.Draw, Is.Zero);
                        Assert.That(appearance.TryGetNockedArrow(out _, out _, out _), Is.False, path);
                        bow.Update(false, arm, 0f);
                        Assert.That(bow.HasLoosed, Is.False, path + " draws again once the clip ends");
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // On both bodies the other hand takes the greataxe's haft where GreataxeSwing brings it near
        // and holds it exactly there, and lets it be in the hold pose.
        [Test]
        public void TheOtherHandTakesTheGreataxeWhereItsSwingBringsItToTheHaft()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                using (var meshes = new PlayerBodyMeshes())
                using (var materials = new PlayerBodyMaterials())
                {
                    foreach (var path in BodyPrefabPaths)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                        var appearance = body.GetComponent<PlayerBodyAppearance>();
                        appearance.CombineParts(meshes);
                        appearance.ApplyLook(BodyLook.Default.WithAttachment("IronGreataxe", true), materials);
                        Assert.That(appearance.Weapon.Name, Is.EqualTo("IronGreataxe"), path);
                        Assert.That(body.GetComponent<CharacterLocomotionAnimator>().HoldClip, Is.EqualTo("HoldGreataxe"));
                        var animator = body.GetComponent<Animator>();
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        var socket = PlayerBodyMeshes.FindDescendant(body.transform, HumanLocomotion.MainHandSocketBone);
                        var offSocket = PlayerBodyMeshes.FindDescendant(body.transform, HumanLocomotion.OffHandSocketBone);
                        var hold = animator.GetLayerIndex(HumanLocomotion.MainHandHoldLayer);
                        var action = animator.GetLayerIndex(HumanLocomotion.ActionLayer);

                        // The strike, GreataxeSwing's frame 15 of 40, has the other hand on the haft.
                        animator.Play(HumanLocomotion.IdleState, 0, 0f);
                        animator.Play("HoldGreataxe", hold, 0f);
                        animator.SetLayerWeight(hold, 1f);
                        animator.Play("GreataxeSwing", action, 15f / 40f);
                        animator.SetLayerWeight(action, 1f);
                        animator.Update(0f);
                        OffHandToolGrip.Target(socket, appearance.Weapon.OffHandGrip, out var grip, out _);
                        var missed = (offSocket.position - grip).magnitude;
                        Assert.That(missed, Is.LessThan(OffHandToolGrip.FarMeters), path + " brings the other hand near the haft");
                        Assert.That(appearance.PinOffHand(1f), Is.GreaterThan(0f), path);
                        Assert.That((offSocket.position - grip).magnitude, Is.LessThan(missed + 0.001f), path);
                        TestContext.WriteLine(path + ": the swing brings the other hand " + (missed * 100f).ToString("F1")
                            + " cm from the haft, the pin " + ((offSocket.position - grip).magnitude * 100f).ToString("F1") + " cm");

                        // In the hold pose the other hand hangs free.
                        animator.SetLayerWeight(action, 0f);
                        animator.Update(0f);
                        Assert.That(appearance.PinOffHand(1f), Is.Zero, path + " pins the free hand on the haft");
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // Plays BowShot at a frame of its 48 over the hold pose; false without the states.
        private static bool Pose(Animator animator, int frame)
        {
            var hold = animator.GetLayerIndex(HumanLocomotion.OffHandHoldLayer);
            var action = animator.GetLayerIndex(HumanLocomotion.ActionLayer);
            if (hold < 0 || action < 0)
            {
                return false;
            }

            animator.Play(HumanLocomotion.IdleState, 0, 0f);
            animator.Play("HoldBow", hold, 0f);
            animator.SetLayerWeight(hold, 1f);
            animator.Play("BowShot", action, frame / 48f);
            animator.SetLayerWeight(action, 1f);
            animator.Update(0f);
            return true;
        }

        private static void AssertMask(AvatarMask mask, params AvatarMaskBodyPart[] active)
        {
            Assert.That(mask, Is.Not.Null);
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                Assert.That(mask.GetHumanoidBodyPartActive(part), Is.EqualTo(active.Contains(part)), mask.name + " " + part);
            }
        }

        private static void AssertClipState(AnimatorControllerLayer layer, string clipName, bool looping)
        {
            var state = layer.stateMachine.states.Select(child => child.state).SingleOrDefault(candidate => candidate.name == clipName);
            Assert.That(state, Is.Not.Null, layer.name + " lacks the state " + clipName);
            var clip = state.motion as AnimationClip;
            Assert.That(clip, Is.Not.Null, clipName);
            Assert.That(clip.name, Is.EqualTo(clipName));
            Assert.That(clip.isLooping, Is.EqualTo(looping), clipName);
            // The body enters and leaves the state, never a transition of its own.
            Assert.That(state.transitions, Is.Empty, clipName);
        }

        private static BodyAppearanceCatalog[] Catalogs()
        {
            var catalogs = new[] { "Human_Male", "Human_Female" }
                .Select(model => AssetDatabase.LoadAssetAtPath<BodyAppearanceCatalog>("Assets/Art/Appearance/" + model + "_Appearance.asset"))
                .ToArray();
            Assert.That(catalogs, Has.None.Null);
            return catalogs;
        }

        // How far a point in a model's space lies from the nearest of its triangles.
        private static float SurfaceDistance(GameObject model, Vector3 point)
        {
            var nearest = float.MaxValue;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                var vertices = filter.sharedMesh.vertices.Select(vertex => toModel.MultiplyPoint3x4(vertex)).ToArray();
                var triangles = filter.sharedMesh.triangles;
                for (var corner = 0; corner < triangles.Length; corner += 3)
                {
                    var closest = ClosestOnTriangle(point, vertices[triangles[corner]], vertices[triangles[corner + 1]], vertices[triangles[corner + 2]]);
                    nearest = Mathf.Min(nearest, (closest - point).magnitude);
                }
            }

            return nearest;
        }

        // The point of a triangle nearest to a point, by its regions (Ericson, Real-Time Collision
        // Detection, 5.1.5).
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            var d1 = Vector3.Dot(ab, ap);
            var d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                return a;
            }

            var bp = p - b;
            var d3 = Vector3.Dot(ab, bp);
            var d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                return b;
            }

            var vc = (d1 * d4) - (d3 * d2);
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                return a + (ab * (d1 / (d1 - d3)));
            }

            var cp = p - c;
            var d5 = Vector3.Dot(ab, cp);
            var d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                return c;
            }

            var vb = (d5 * d2) - (d1 * d6);
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                return a + (ac * (d2 / (d2 - d6)));
            }

            var va = (d3 * d6) - (d5 * d4);
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
            }

            var denominator = 1f / (va + vb + vc);
            return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
        }

        // Every vertex of a model in its own space, which is its socket's; a skinned model's in its
        // bind pose.
        private static IEnumerable<Vector3> ModelVertices(GameObject model)
        {
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>())
            {
                var toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                foreach (var vertex in filter.sharedMesh.vertices)
                {
                    yield return toModel.MultiplyPoint3x4(vertex);
                }
            }

            foreach (var skinned in model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var toModel = model.transform.worldToLocalMatrix * skinned.transform.localToWorldMatrix;
                foreach (var vertex in skinned.sharedMesh.vertices)
                {
                    yield return toModel.MultiplyPoint3x4(vertex);
                }
            }
        }
    }
}
