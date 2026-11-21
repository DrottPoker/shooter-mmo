"""The map of a clip import, tools/art/retarget/*.json, and which Humanoid bones the retarget aligns to the
source's rest pose: the rules a map must meet, checked on small maps and on every shipped map against the
Humanoid skeleton definition. Runs without Blender: see blender_source.py."""

import json
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

retarget = load("human_base/retarget.py", "map_errors", "aligned_pairs", "hand_sides", "keyed", "distinct_frames", "REQUIRED",
                "LIMBS", "REFIT_LIMBS", "FINGERS", "SEGMENTS", "HAND_FRAME", "GRIPS", "SOURCE_TYPES")
MAPS = REPOSITORY_ROOT / "tools" / "art" / "retarget"
HUMANOID = json.loads((REPOSITORY_ROOT / "art" / "Skeletons" / "Humanoid.json").read_text(encoding="utf-8"))
HUMANOID_BONES = {bone["name"] for bone in HUMANOID["bones"]}
CHAIN_BONES = {bone["name"] for bone in HUMANOID["bones"] if bone.get("chain")}
LEGS = {bone: bone.lower() for bone in retarget.REQUIRED}


def errors(data, source=None):
    return retarget.map_errors(data, HUMANOID_BONES, set(source if source is not None else LEGS.values()))


class MapRuleTests(unittest.TestCase):
    def test_a_map_of_the_hips_and_the_legs_with_a_facing_is_enough(self):
        self.assertEqual(errors({"facing": 180, "bones": LEGS}), [])
        self.assertEqual(errors({"facing": -90.5, "bones": LEGS}), [])

    def test_a_map_without_bones_says_what_it_needs(self):
        for data in (None, [], {}, {"facing": 0, "bones": ["Hips"]}):
            with self.subTest(data=data):
                self.assertEqual(len(errors(data)), 1)
                self.assertIn('"bones"', errors(data)[0])

    def test_the_facing_is_a_number_of_degrees(self):
        for facing in (None, "180", True, float("nan")):
            with self.subTest(facing=facing):
                self.assertEqual(len(errors({"facing": facing, "bones": LEGS})), 1)
                self.assertIn('"facing"', errors({"facing": facing, "bones": LEGS})[0])

    def test_every_required_bone_is_named_and_the_missing_ones_are_listed(self):
        bones = {k: v for k, v in LEGS.items() if k not in ("Hips", "Foot_L")}
        self.assertEqual(errors({"facing": 0, "bones": bones}), ["The map must name the source bone of Hips, Foot_L."])

    def test_a_bone_that_neither_skeleton_has_is_named(self):
        found = errors({"facing": 0, "bones": dict(LEGS, Tail="tail_01")}, set(LEGS.values()))
        self.assertEqual(found, ['The map names "Tail", which is no bone of Humanoid.',
                                 'The map names "tail_01" for Tail, which is no bone of the source skeleton.'])
        found = errors({"facing": 0, "bones": dict(LEGS, Head="head")})
        self.assertEqual(found, ['The map names "head" for Head, which is no bone of the source skeleton.'])

    def test_the_skeleton_is_named_in_the_message(self):
        found = retarget.map_errors({"facing": 0, "bones": dict(LEGS, Wing="wing")}, HUMANOID_BONES,
                                    set(LEGS.values()) | {"wing"}, "Avian")
        self.assertEqual(found, ['The map names "Wing", which is no bone of Avian.'])


class AlignmentTests(unittest.TestCase):
    def test_the_legs_of_a_minimal_map_are_aligned_and_nothing_else(self):
        self.assertEqual(retarget.aligned_pairs(LEGS),
                         [("UpperLeg_L", "LowerLeg_L"), ("LowerLeg_L", "Foot_L"), ("UpperLeg_R", "LowerLeg_R"),
                          ("LowerLeg_R", "Foot_R")])
        self.assertEqual(retarget.hand_sides(LEGS), [])

    def test_a_finger_segment_points_to_the_next_and_the_last_to_its_own_tail(self):
        bones = dict.fromkeys(["IndexProximal_R", "IndexIntermediate_R", "IndexDistal_R"], "x")
        self.assertEqual(retarget.aligned_pairs(bones), [("IndexProximal_R", "IndexIntermediate_R"),
                                                         ("IndexIntermediate_R", "IndexDistal_R"), ("IndexDistal_R", None)])

    def test_a_segment_whose_next_segment_is_not_mapped_is_not_aligned(self):
        bones = dict.fromkeys(["ThumbProximal_L", "ThumbDistal_L"], "x")
        self.assertEqual(retarget.aligned_pairs(bones), [("ThumbDistal_L", None)])

    def test_a_hand_takes_the_source_hand_s_frame_only_with_its_finger_bases(self):
        bones = dict.fromkeys(["Hand_L"] + [f"{b}_L" for b in retarget.HAND_FRAME] + ["Hand_R", "MiddleProximal_R"], "x")
        self.assertEqual(retarget.hand_sides(bones), ["L"])

    def test_the_tables_name_humanoid_bones_that_clips_move(self):
        sided = list(retarget.LIMBS) + list(retarget.LIMBS.values()) + list(retarget.HAND_FRAME)
        sided += [f"{finger}{segment}" for finger in retarget.FINGERS for segment in retarget.SEGMENTS]
        names = set(retarget.REQUIRED) | {f"{bone}_{side}" for bone in sided for side in "LR"}
        self.assertEqual(sorted(names - HUMANOID_BONES), [])
        self.assertEqual(sorted(names & CHAIN_BONES), [])


class RefitTests(unittest.TestCase):
    """What a refit reads from a clip, and which bones it aligns to the old rig's rest pose."""

    def test_a_refit_aligns_the_arms_and_the_legs_and_leaves_the_feet_to_the_ground(self):
        bones = {bone: bone for bone in HUMANOID_BONES - CHAIN_BONES}
        pairs = retarget.aligned_pairs(bones, retarget.REFIT_LIMBS)
        self.assertIn(("UpperArm_R", "LowerArm_R"), pairs)
        self.assertIn(("LowerLeg_L", "Foot_L"), pairs)
        self.assertEqual([pair for pair in pairs if pair[0].startswith("Foot_")], [])
        # An imported clip aligns the feet too.
        self.assertEqual(set(retarget.aligned_pairs(bones)) - set(pairs), {("Foot_L", "Toes_L"), ("Foot_R", "Toes_R")})

    def test_a_clip_keys_the_bones_it_turns_and_the_hips_it_moves(self):
        paths = ['pose.bones["Hips"].rotation_quaternion', 'pose.bones["Hips"].location',
                 'pose.bones["IndexDistal_L"].rotation_quaternion', 'pose.bones["IndexDistal_L"].rotation_quaternion']
        self.assertEqual(retarget.keyed(paths), ({"Hips", "IndexDistal_L"}, True, []))
        self.assertEqual(retarget.keyed(['pose.bones["ThumbDistal_R"].rotation_quaternion']), ({"ThumbDistal_R"}, False, []))
        self.assertEqual(retarget.keyed([]), (set(), False, []))

    def test_curves_a_refit_cannot_move_are_named_once_each(self):
        paths = ['pose.bones["Hand_L"].location', 'pose.bones["Hand_L"].location', 'pose.bones["Head"].scale',
                 'pose.bones["Spine"].rotation_euler', "location", 'pose.bones["Spine"].rotation_quaternion']
        bones, hips, other = retarget.keyed(paths)
        self.assertEqual((bones, hips), ({"Spine"}, False))
        self.assertEqual(other, ["location", 'pose.bones["Hand_L"].location', 'pose.bones["Head"].scale',
                                 'pose.bones["Spine"].rotation_euler'])

    def test_a_looping_clip_plays_one_frame_less_than_it_keys(self):
        # As the gait model keys: a loop's last key is its first again, one frame later.
        self.assertEqual(retarget.distinct_frames(0, 28, True), 28)
        self.assertEqual(retarget.distinct_frames(0, 1, True), 1)
        self.assertEqual(retarget.distinct_frames(0, 60, False), 61)
        self.assertEqual(retarget.distinct_frames(0, 0, False), 1)
        self.assertEqual(retarget.distinct_frames(0, 0, True), 0)


class ShippedMapTests(unittest.TestCase):
    def test_every_shipped_map_meets_the_rules_against_the_humanoid(self):
        maps = sorted(MAPS.glob("*.json"))
        self.assertTrue(maps)
        for path in maps:
            data = json.loads(path.read_text(encoding="utf-8"))
            with self.subTest(map=path.name):
                self.assertEqual(errors(data, set(data["bones"].values())), [])
                self.assertEqual(sorted(set(data["bones"]) & CHAIN_BONES), [])
                self.assertEqual(len(set(data["bones"].values())), len(data["bones"]))

    def test_the_universal_animation_library_map_turns_the_source_around_and_moves_the_fingers(self):
        data = json.loads((MAPS / "UniversalAnimationLibrary.json").read_text(encoding="utf-8"))
        self.assertEqual(data["facing"], 180)
        self.assertEqual(data["bones"]["ThumbProximal_L"], "thumb_01_l")
        self.assertEqual(data["bones"]["LittleDistal_R"], "pinky_03_r")
        self.assertEqual(retarget.hand_sides(data["bones"]), ["L", "R"])
        # Both arms and legs with their feet, and three segments of five fingers on both hands.
        self.assertEqual(len(retarget.aligned_pairs(data["bones"])), 2 * (5 + 5 * 3))


class ArgumentTests(unittest.TestCase):
    def test_the_grip_and_the_source_types_match_the_script(self):
        script = (REPOSITORY_ROOT / "tools" / "art" / "Import-ArtClip.ps1").read_text(encoding="utf-8")
        self.assertIn('[ValidateSet("' + '", "'.join(g for g in retarget.GRIPS if g != "-") + '")]', script)
        self.assertIn("@(" + ", ".join(f'"{t}"' for t in retarget.SOURCE_TYPES) + ")", script)


if __name__ == "__main__":
    unittest.main()
