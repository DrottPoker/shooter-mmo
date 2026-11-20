"""The bones that the rig, the test poses, and the gait model of the human base characters name must be
bones of art/Skeletons/Humanoid.json, and what the clips and the speeds say must be what the exported
clips and the Unity client say. Runs without Blender: see blender_source.py."""

import json
import math
import pathlib
import re
import sys
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load, source_text  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
bodies = load("art_pipeline/bodies.py", "ZONES")
bodies_of = load("art_pipeline/bodies.py", "body_name")
cage = load("art_pipeline/cage.py", "DIGITS")
poses = load("art_pipeline/poses.py", "POSES", "PLANT_FEET", "FINGERS", "_fist", "rest")
rig = load("human_base/rig.py", "ZONE_BONES", "RULE_BONES", "HINGES", "DIGITS", "SEGMENTS", "SPINE_SHARES", "SPINE_INSETS",
           "HIP_BOUNDARY", "HIP_BOUNDARY_REBUILT", "HIP_REGION", "HIP_REGION_REBUILT", "HIP_ABOVE_CROTCH",
           "HIP_ABOVE_CROTCH_REBUILT", "HIP_EVENING_REBUILT", "CROTCH_RADIUS", "CROTCH_MIDDLE_REBUILT", "SKIRT_CHAINS",
           "SKIRT_BONES", "CLOAK_CHAINS", "CLOAK_BONES", "HOOD_STEPS", "SIDES",
           "MAX_INFLUENCES", "MIN_WEIGHT", "FINGER_LAYERS", "THUMB_LAYERS", "RING_SIZE", bodies=bodies)
animate = load("human_base/animate.py", "Gait", "WALK", "JOG", "CLIPS", "WALK_SPEED", "JOG_SPEED", "IDLE_FRAMES", "FPS",
               "RELAXED_FINGERS", "RELAXED_THUMB", "SEGMENTS", "SIDES", "_ease", "_lift", "_hermite", "_soft_min",
               "gait_frames", "REACH", "SOFT_MIN", poses=poses)
preview = load("art_pipeline/preview.py", "GARMENT_POSES")
skinned = load("art_pipeline/skinned.py", "MAX_INFLUENCES", "FRAME_RATE")
library = load("art_pipeline/library.py", "FRAME_RATE")
checks = load("human_base/checks.py", "CHARACTERS", "BODIES", "_mirror_name", "TINY_AREA", "TINY_AREA_HIDDEN", "HIDDEN_PARTS")
files = load("human_base/files.py", "LABELS")
review = load("human_base/review.py", "BODIES")
rig_review = load("human_base/rig_review.py", "WEIGHT_VIEWS")
garment = load("art_pipeline/garment.py", "MAX_INFLUENCES")

SKELETON = json.loads((REPOSITORY_ROOT / "art" / "Skeletons" / "Humanoid.json").read_text(encoding="utf-8"))
BONES = {bone["name"]: bone for bone in SKELETON["bones"]}


def expanded(name: str) -> list[str]:
    """A bone name of the tables, "_s" standing for both sides."""
    return [f"{name[:-2]}_L", f"{name[:-2]}_R"] if name.endswith("_s") else [name]


class PoseTests(unittest.TestCase):
    def test_every_pose_has_its_own_name_and_a_description(self):
        names = [name for name, _description, _turns in poses.POSES]
        self.assertEqual(len(names), len(set(names)))
        self.assertEqual(names[0], "rest")
        for name, description, _turns in poses.POSES:
            self.assertTrue(description, name)

    def test_the_rest_pose_turns_no_bone(self):
        self.assertEqual(dict(poses.POSES[0][2]), {})

    def test_every_bone_a_pose_turns_is_a_bone_of_the_humanoid(self):
        for name, _description, turns in poses.POSES:
            for bone in turns:
                if bone == poses.PLANT_FEET:
                    continue
                for expanded_name in expanded(bone):
                    with self.subTest(pose=name, bone=expanded_name):
                        self.assertIn(expanded_name, BONES)

    def test_a_turn_is_about_one_of_three_axes_by_a_sensible_angle(self):
        for name, _description, turns in poses.POSES:
            for bone, bone_turns in turns.items():
                if bone == poses.PLANT_FEET:
                    self.assertIs(bone_turns, True)
                    continue
                for axis, degrees in bone_turns:
                    with self.subTest(pose=name, bone=bone):
                        self.assertIn(axis, ("X", "Y", "Z"))
                        self.assertLessEqual(abs(degrees), 180)

    def test_a_fist_curls_all_four_fingers_and_the_thumb_and_curl_zero_is_a_flat_hand(self):
        fist = poses._fist()
        self.assertEqual(len(fist), 4 * 3 + 3)
        flat = poses._fist(0.0)
        for bone, turns in flat.items():
            for _axis, degrees in turns:
                self.assertEqual(degrees, 0)
        half = poses._fist(0.5)
        for bone, turns in fist.items():
            for (axis, full), (half_axis, part) in zip(turns, half[bone]):
                self.assertEqual(axis, half_axis)
                self.assertAlmostEqual(part, full / 2)

    def test_the_fingers_of_the_pose_tables_are_the_fingers_of_the_skeleton(self):
        self.assertEqual(set(poses.FINGERS) | {"Thumb"}, set(cage.DIGITS))

    def test_the_poses_a_garment_is_reviewed_in_are_poses(self):
        names = {name for name, _description, _turns in poses.POSES}
        self.assertTrue(set(preview.GARMENT_POSES) <= names)
        self.assertEqual(len(preview.GARMENT_POSES), len(set(preview.GARMENT_POSES)))

    def test_a_pose_that_plants_the_feet_is_only_the_sitting_one(self):
        planting = [name for name, _d, turns in poses.POSES if turns.get(poses.PLANT_FEET)]
        self.assertEqual(planting, ["sit"])


class RigTableTests(unittest.TestCase):
    def test_the_zones_that_may_use_bones_are_the_body_parts(self):
        self.assertEqual(list(rig.ZONE_BONES), list(bodies.ZONES))

    def test_every_bone_a_zone_or_a_rule_names_is_a_deforming_bone(self):
        for zone, names in rig.ZONE_BONES.items():
            for name in names:
                for bone in expanded(name):
                    with self.subTest(zone=zone, bone=bone):
                        self.assertTrue(BONES[bone]["deform"])
        for bone in rig.RULE_BONES:
            self.assertTrue(BONES[bone]["deform"])

    def test_the_deforming_bones_no_zone_or_rule_names_are_the_finger_bones_and_the_eyelids(self):
        used = {bone for names in rig.ZONE_BONES.values() for name in names for bone in expanded(name)} | set(rig.RULE_BONES)
        left = {name for name, bone in BONES.items() if bone["deform"] and not bone.get("chain")} - used
        fingers = {f"{digit}{segment}_{side}" for digit in rig.DIGITS for segment in rig.SEGMENTS for side in ("L", "R")}
        # The eyelids take their weights by the rule of the skeleton's eyelids section.
        lids = {eye[lid] for eye in SKELETON["eyelids"]["eyes"] for lid in ("upper", "lower")}
        self.assertEqual(left, fingers | lids)

    def test_the_digits_and_segments_are_those_of_the_cage_and_the_gait_model(self):
        self.assertEqual(set(rig.DIGITS), set(cage.DIGITS))
        self.assertEqual(rig.SEGMENTS, animate.SEGMENTS)
        self.assertEqual(rig.SIDES, animate.SIDES)

    def test_the_hinges_name_the_bones_and_zones_of_a_limb(self):
        for upper, lower, zones, _fold, *widths in rig.HINGES:
            with self.subTest(hinge=upper):
                for side in ("L", "R"):
                    self.assertEqual(BONES[f"{lower}_{side}"]["parent"], f"{upper}_{side}")
                self.assertTrue(set(zones) <= set(bodies.ZONES))
                self.assertTrue(all(w > 0 for w in widths))
                self.assertEqual(sorted(widths)[0], widths[0])  # the fold's blend is the shortest

    def test_the_spine_bones_lie_in_order_between_the_hips_and_the_neck(self):
        self.assertEqual(list(rig.SPINE_SHARES), sorted(rig.SPINE_SHARES))
        self.assertTrue(0.0 < rig.SPINE_SHARES[0] and rig.SPINE_SHARES[-1] < 1.0)
        self.assertEqual(len(rig.SPINE_INSETS), len(rig.SPINE_SHARES))
        self.assertEqual([BONES[n]["parent"] for n in ("Spine", "Chest", "UpperChest")], ["Hips", "Spine", "Chest"])

    def test_the_hip_boundary_goes_round_the_thigh_in_quarter_turns(self):
        self.assertEqual([angle for angle, _below, _blend in rig.HIP_BOUNDARY], [0, 90, 180, 270])
        self.assertTrue(all(blend > 0 for _angle, _below, blend in rig.HIP_BOUNDARY))

    def test_the_rebuilt_bodies_hip_boundary_goes_once_round_the_thigh_from_the_front(self):
        angles = [angle for angle, _below, _blend in rig.HIP_BOUNDARY_REBUILT]
        self.assertEqual(angles[0], 0)
        self.assertEqual(angles, sorted(set(angles)))
        self.assertLess(angles[-1], 360)
        self.assertTrue(all(blend > 0 for _angle, _below, blend in rig.HIP_BOUNDARY_REBUILT))

    def test_the_rebuilt_bodies_hip_joint_lies_higher_and_its_shaping_reaches_as_far_down_the_thigh(self):
        raised = rig.HIP_ABOVE_CROTCH_REBUILT - rig.HIP_ABOVE_CROTCH
        self.assertGreater(raised, 0.0)
        # The reach is measured from the joint, so it grows by what the joint was raised.
        self.assertGreaterEqual(rig.HIP_REGION_REBUILT, rig.HIP_REGION + raised)
        times, distance = rig.HIP_EVENING_REBUILT
        self.assertGreater(times, 0)
        self.assertGreater(distance, raised)

    def test_the_rebuilt_bodies_thighs_reach_their_share_beyond_the_middle_of_the_crotch(self):
        half_width, height = rig.CROTCH_MIDDLE_REBUILT
        self.assertTrue(0.0 < half_width < rig.CROTCH_RADIUS)
        self.assertGreater(height, 0.0)
        # The middle line climbs the front to the hip joints, which lie above what counts as the underside.
        self.assertLess(height, rig.HIP_ABOVE_CROTCH_REBUILT)

    def test_a_vertex_has_at_most_as_many_influences_as_every_rule_allows(self):
        self.assertEqual(rig.MAX_INFLUENCES, skinned.MAX_INFLUENCES)
        self.assertEqual(rig.MAX_INFLUENCES, garment.MAX_INFLUENCES)
        self.assertTrue(0.0 < rig.MIN_WEIGHT < 1.0 / rig.MAX_INFLUENCES)

    def test_a_digit_has_rings_of_eight_with_its_joints_before_its_base(self):
        self.assertEqual(rig.RING_SIZE, 8)
        self.assertLess(rig.FINGER_LAYERS["dip"], rig.FINGER_LAYERS["pip"])
        self.assertLess(rig.FINGER_LAYERS["pip"], rig.FINGER_LAYERS["base"])
        self.assertLess(rig.THUMB_LAYERS["ip"], rig.THUMB_LAYERS["mcp"])


class ClothChainTableTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.kinds = {kind["kind"]: kind for kind in SKELETON["cloth"]["kinds"]}

    def _check(self, chains, kind: str, bones: int):
        data = self.kinds[kind]
        self.assertEqual(bones, data["bones"])
        self.assertEqual([name for name, *_ in chains], [chain["name"] for chain in data["chains"]])
        for (name, _place, suffix), chain in zip(chains, data["chains"]):
            self.assertEqual(suffix == "_R", chain["sided"], name)
            for number in range(1, bones + 1):
                for side in (("_L", "_R") if chain["sided"] else ("",)):
                    self.assertEqual(BONES[f"{name}{number}{side}"]["chain"], kind)

    def test_the_skirt_chains_the_rig_makes_are_the_skirt_chains_of_the_skeleton(self):
        self._check(rig.SKIRT_CHAINS, "skirt", rig.SKIRT_BONES)
        angles = [degrees for _name, degrees, _suffix in rig.SKIRT_CHAINS]
        self.assertEqual(angles, [chain["angle"] for chain in self.kinds["skirt"]["chains"]])

    def test_the_cloak_chains_the_rig_makes_are_the_cloak_chains_of_the_skeleton(self):
        self._check(rig.CLOAK_CHAINS, "cloak", rig.CLOAK_BONES)
        offsets = [x for _name, x, _suffix in rig.CLOAK_CHAINS]
        self.assertEqual(offsets, sorted(offsets))

    def test_the_hood_chain_has_a_step_for_each_of_its_bones(self):
        self.assertEqual(len(rig.HOOD_STEPS), self.kinds["hood"]["bones"])
        for number in range(1, len(rig.HOOD_STEPS) + 1):
            self.assertEqual(BONES[f"Hood{number}"]["chain"], "hood")


class GaitTests(unittest.TestCase):
    def test_the_speeds_are_the_speeds_the_client_plays_the_clips_at(self):
        text = (REPOSITORY_ROOT / "src/Client/Assets/Scripts/Gameplay/Animation/HumanLocomotion.cs").read_text(encoding="utf-8")
        walk = float(re.search(r"WalkSpeed\s*=\s*([0-9.]+)f", text).group(1))
        jog = float(re.search(r"JogSpeed\s*=\s*([0-9.]+)f", text).group(1))
        self.assertEqual((animate.WALK_SPEED, animate.JOG_SPEED), (walk, jog))
        self.assertEqual(animate.CLIPS, {"Idle": 0.0, "Walk": walk, "Jog": jog})
        self.assertEqual((animate.WALK.speed, animate.JOG.speed), (walk, jog))

    def test_the_clips_the_model_makes_are_clips_of_the_animation_library(self):
        library = json.loads((REPOSITORY_ROOT / "art/Animations/Human_Actions/asset.json").read_text(encoding="utf-8"))
        self.assertTrue(set(animate.CLIPS) <= set(library["clips"]))
        frames = {clip["name"]: clip for clip in library["export"]["clips"]}
        self.assertEqual(frames["Idle"]["frames"], animate.IDLE_FRAMES)
        for name in animate.CLIPS:
            self.assertTrue(frames[name]["loop"], name)

    def test_the_length_of_a_cycle_for_the_leg_of_the_human_rig_is_the_exported_length(self):
        # The human rig's leg, hip joint to ankle, is about 0.78 m: the cycle rounds to the exported
        # 28 and 19 frames for the legs from 0.771 to 0.782 m.
        library = json.loads((REPOSITORY_ROOT / "art/Animations/Human_Actions/asset.json").read_text(encoding="utf-8"))
        frames = {clip["name"]: clip["frames"] for clip in library["export"]["clips"]}
        for leg in (0.772, 0.777, 0.781):
            rig_of_leg = types.SimpleNamespace(leg=leg)
            with self.subTest(leg=leg):
                self.assertEqual(animate.gait_frames(rig_of_leg, animate.WALK), frames["Walk"])
                self.assertEqual(animate.gait_frames(rig_of_leg, animate.JOG), frames["Jog"])

    def test_a_cycle_is_two_steps_at_the_gait_s_speed_and_never_shorter_than_eight_frames(self):
        leg = types.SimpleNamespace(leg=1.0)
        self.assertEqual(animate.gait_frames(leg, animate.WALK), round(2.0 * 0.85 * 1.0 / 1.4 * 30))
        self.assertEqual(animate.gait_frames(types.SimpleNamespace(leg=0.01), animate.WALK), 8)
        longer = animate.gait_frames(types.SimpleNamespace(leg=2.0), animate.JOG)
        self.assertEqual(longer, 2 * animate.gait_frames(types.SimpleNamespace(leg=1.0), animate.JOG))

    def test_a_gait_has_a_value_for_every_field_and_a_stance_that_leaves_a_swing(self):
        import dataclasses
        for gait in (animate.WALK, animate.JOG):
            for field in dataclasses.fields(gait):
                self.assertIsInstance(getattr(gait, field.name), float, field.name)
            self.assertTrue(0.0 < gait.stance < 1.0)
            self.assertTrue(0.0 < gait.lift_peak < 1.0)
            self.assertTrue(0.0 <= gait.heel_off < 1.0)
            self.assertTrue(0.0 <= gait.landing <= 1.0)

    def test_a_jog_has_a_longer_step_a_shorter_stance_and_a_higher_lift_than_a_walk(self):
        self.assertGreater(animate.JOG.step, animate.WALK.step)
        self.assertLess(animate.JOG.stance, animate.WALK.stance)
        self.assertGreater(animate.JOG.lift, animate.WALK.lift)
        # Walking, both feet are on the ground part of the cycle; jogging, none is.
        self.assertGreater(2 * animate.WALK.stance, 1.0)
        self.assertLess(2 * animate.JOG.stance, 1.0)

    def test_the_relaxed_hand_curls_each_finger_more_than_the_one_before_it(self):
        self.assertEqual(set(animate.RELAXED_FINGERS), set(poses.FINGERS))
        first = [sum(animate.RELAXED_FINGERS[f]) for f in poses.FINGERS]
        self.assertEqual(first, sorted(first))
        self.assertEqual(len(animate.RELAXED_THUMB), len(animate.SEGMENTS))

    def test_the_ease_goes_from_zero_to_one_and_is_flat_at_both_ends(self):
        self.assertEqual(animate._ease(0.0), 0.0)
        self.assertEqual(animate._ease(1.0), 1.0)
        self.assertEqual(animate._ease(-5.0), 0.0)
        self.assertEqual(animate._ease(5.0), 1.0)
        self.assertAlmostEqual(animate._ease(0.5), 0.5)
        steps = [animate._ease(i / 100) for i in range(101)]
        self.assertEqual(steps, sorted(steps))
        self.assertLess(animate._ease(0.01), 1e-5)
        self.assertGreater(animate._ease(0.99), 1 - 1e-5)

    def test_the_lift_is_zero_at_both_ends_and_one_at_its_peak(self):
        for peak in (0.4, 0.45, 0.5):
            with self.subTest(peak=peak):
                self.assertAlmostEqual(animate._lift(0.0, peak), 0.0)
                self.assertAlmostEqual(animate._lift(peak, peak), 1.0)
                self.assertAlmostEqual(animate._lift(1.0, peak), 0.0)
                values = [animate._lift(i / 100, peak) for i in range(101)]
                self.assertLessEqual(max(values), 1.0 + 1e-12)
                self.assertGreaterEqual(min(values), 0.0)

    def test_a_hermite_cubic_starts_and_ends_where_it_is_told_with_the_slopes_it_is_told(self):
        y0, y1, m0, m1 = 2.0, 5.0, -1.0, 3.0
        self.assertAlmostEqual(animate._hermite(y0, y1, m0, m1, 0.0), y0)
        self.assertAlmostEqual(animate._hermite(y0, y1, m0, m1, 1.0), y1)
        step = 1e-6
        self.assertAlmostEqual((animate._hermite(y0, y1, m0, m1, step) - y0) / step, m0, places=4)
        self.assertAlmostEqual((y1 - animate._hermite(y0, y1, m0, m1, 1.0 - step)) / step, m1, places=4)
        # With zero slopes and equal ends it stays put; a straight line has its own slope all the way.
        self.assertAlmostEqual(animate._hermite(1.0, 1.0, 0.0, 0.0, 0.37), 1.0)
        self.assertAlmostEqual(animate._hermite(0.0, 2.0, 2.0, 2.0, 0.25), 0.5)

    def test_a_soft_minimum_lies_just_below_the_least_value_and_below_all_of_them(self):
        # A value far above the least one does not count; two equal ones lower it by the width times ln 2.
        self.assertAlmostEqual(animate._soft_min([1.0, 5.0], 0.005), 1.0)
        self.assertAlmostEqual(animate._soft_min([1.0, 1.0], 0.005), 1.0 - 0.005 * math.log(2))
        self.assertAlmostEqual(animate._soft_min([2.0], 0.01), 2.0)
        # The narrower the width, the nearer the hard minimum.
        self.assertGreater(animate._soft_min([1.0, 1.0], 0.001), animate._soft_min([1.0, 1.0], 0.1))


class HumanBaseTableTests(unittest.TestCase):
    def test_every_module_names_the_same_two_characters_and_their_bodies(self):
        self.assertEqual(checks.CHARACTERS, ("Male", "Female"))
        self.assertEqual(files.LABELS, checks.CHARACTERS)
        self.assertEqual(review.BODIES, checks.CHARACTERS)
        self.assertEqual(checks.BODIES, tuple(bodies_of.body_name(label) for label in checks.CHARACTERS))

    def test_the_characters_are_the_armatures_of_the_asset_manifests(self):
        for folder in ("Human_Male", "Human_Female"):
            manifest = json.loads((REPOSITORY_ROOT / "art" / "Characters" / folder / "asset.json").read_text(encoding="utf-8"))
            self.assertEqual(manifest["rootObjects"], [folder.removeprefix("Human_")])
            self.assertIn(folder.removeprefix("Human_"), checks.CHARACTERS)

    def test_every_clip_and_every_script_works_at_thirty_frames_a_second(self):
        self.assertEqual({animate.FPS, skinned.FRAME_RATE, library.FRAME_RATE}, {30})

    def test_a_bone_names_its_mirror_by_swapping_the_side_and_a_centre_bone_is_its_own(self):
        self.assertEqual(checks._mirror_name("UpperArm_L"), "UpperArm_R")
        self.assertEqual(checks._mirror_name("UpperArm_R"), "UpperArm_L")
        self.assertEqual(checks._mirror_name("Hips"), "Hips")
        self.assertEqual(checks._mirror_name("Eye"), "Eye")
        # Every bone of the humanoid has a mirror bone that is a bone of the humanoid.
        for name in BONES:
            self.assertIn(checks._mirror_name(name), BONES)
            self.assertEqual(checks._mirror_name(checks._mirror_name(name)), name)

    def test_a_hidden_part_may_have_a_smaller_face_than_the_rest_of_the_body(self):
        self.assertLess(checks.TINY_AREA_HIDDEN, checks.TINY_AREA)
        self.assertEqual(len(set(checks.HIDDEN_PARTS)), len(checks.HIDDEN_PARTS))

    def test_the_bones_the_weights_sheet_looks_at_are_bones_of_the_humanoid(self):
        for bone, target, _yaw, _pitch, _distance in rig_review.WEIGHT_VIEWS:
            self.assertIn(bone, BONES)
            self.assertIn(target, BONES)

    def test_the_review_views_name_poses_and_bones_that_exist(self):
        # A view is ("<pose>", "<Bone>", ...): a typo would stop the sheet at the first lookup.
        pose_names = {name for name, _description, _turns in poses.POSES}
        views = re.findall(r'\("([a-z_]+)", "([A-Za-z_]+)", [-0-9.]+, ', source_text("human_base/rig_review.py"))
        self.assertGreater(len(views), 10)
        for pose, bone in views:
            with self.subTest(pose=pose, bone=bone):
                self.assertIn(pose, pose_names)
                self.assertIn(bone, set(BONES) | {"Eyes"})


if __name__ == "__main__":
    unittest.main()
