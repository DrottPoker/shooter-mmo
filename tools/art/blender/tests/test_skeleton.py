"""The skeleton definitions art/Skeletons/<Name>.json are read with the rules skeleton.py states, and
the tables of the pipeline that name bones agree with them. Runs without Blender: see
blender_source.py."""

import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

SKELETONS = REPOSITORY_ROOT / "art" / "Skeletons"
scene = load("art_pipeline/scene.py", "ArtPipelineError")
skeleton = load("art_pipeline/skeleton.py", "load", "Skeleton", "SkeletonBone", "CHAINS",
                ArtPipelineError=scene.ArtPipelineError)


def humanoid():
    return skeleton.load(str(SKELETONS / "Humanoid.json"))


class SkeletonLoadTests(unittest.TestCase):
    def _write(self, folder: str, bones: list[dict], name: str = "Test") -> str:
        path = pathlib.Path(folder) / f"{name}.json"
        path.write_text(json.dumps({"schemaVersion": 1, "name": name, "bones": bones}), encoding="utf-8", newline="\n")
        return str(path)

    def _error(self, bones: list[dict]) -> str:
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(scene.ArtPipelineError) as raised:
                skeleton.load(self._write(folder, bones))
        return str(raised.exception)

    def test_a_small_skeleton_keeps_its_bones_in_order_with_their_fields(self):
        with tempfile.TemporaryDirectory() as folder:
            loaded = skeleton.load(self._write(folder, [
                {"name": "Hips", "parent": None, "human": "Hips", "deform": True},
                {"name": "Socket_MainHand", "parent": "Hips", "socket": "MainHand", "deform": False},
                {"name": "SkirtFront1", "parent": "Hips", "chain": "skirt", "deform": True},
            ], name="Small"))
        self.assertEqual(loaded.name, "Small")
        self.assertEqual([b.name for b in loaded.bones], ["Hips", "Socket_MainHand", "SkirtFront1"])
        self.assertEqual(loaded.root.name, "Hips")
        self.assertEqual(loaded.deform_bones, ["Hips", "SkirtFront1"])
        self.assertEqual(loaded.chain_bones, {"SkirtFront1"})
        self.assertEqual(loaded.bone("Socket_MainHand").socket, "MainHand")
        self.assertIsNone(loaded.bone("Hips").chain)

    def test_a_bone_name_used_twice_is_refused(self):
        error = self._error([{"name": "A", "parent": None, "deform": True}, {"name": "A", "parent": "A", "deform": True}])
        self.assertIn("unique bone names and one root", error)

    def test_a_skeleton_needs_exactly_one_root(self):
        self.assertIn("one root", self._error([{"name": "A", "parent": None, "deform": True},
                                               {"name": "B", "parent": None, "deform": True}]))
        self.assertIn("one root", self._error([{"name": "A", "parent": "B", "deform": True},
                                               {"name": "B", "parent": "A", "deform": True}]))

    def test_a_bone_must_follow_its_parent(self):
        error = self._error([{"name": "A", "parent": None, "deform": True}, {"name": "C", "parent": "B", "deform": True},
                             {"name": "B", "parent": "A", "deform": True}])
        self.assertIn('"C"', error)
        self.assertIn('"B"', error)

    def test_a_chain_bone_must_deform_and_stay_out_of_the_avatar_and_the_sockets(self):
        root = {"name": "Hips", "parent": None, "deform": True}
        for extra in ({"chain": "skirt", "deform": False}, {"chain": "skirt", "deform": True, "human": "Spine"},
                      {"chain": "skirt", "deform": True, "socket": "Back"}, {"chain": "wing", "deform": True}):
            with self.subTest(extra=extra):
                error = self._error([root, {"name": "Skirt1", "parent": "Hips", **extra}])
                self.assertIn('"Skirt1"', error)
                self.assertIn("chains", error)

    def test_a_missing_or_malformed_file_is_a_pipeline_error(self):
        with tempfile.TemporaryDirectory() as folder:
            broken = pathlib.Path(folder) / "broken.json"
            broken.write_text("{ not json", encoding="utf-8")
            for path in (broken, pathlib.Path(folder) / "missing.json"):
                with self.subTest(path=path.name), self.assertRaisesRegex(scene.ArtPipelineError, "Cannot read the skeleton"):
                    skeleton.load(str(path))


class SkeletonFileTests(unittest.TestCase):
    def test_every_skeleton_file_loads_and_is_named_after_its_file(self):
        files = sorted(SKELETONS.glob("*.json"))
        self.assertEqual([f.stem for f in files], ["Bow", "Humanoid", "Quadruped"])
        for file in files:
            with self.subTest(file=file.name):
                self.assertEqual(skeleton.load(str(file)).name, file.stem)

    def test_the_humanoid_maps_each_unity_avatar_bone_at_most_once(self):
        mapped = [b.human for b in humanoid().bones if b.human]
        self.assertEqual(len(mapped), len(set(mapped)))
        # The bones the Unity Humanoid avatar requires.
        required = {"Hips", "Spine", "Head", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightUpperArm",
                    "RightLowerArm", "RightHand", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "RightUpperLeg",
                    "RightLowerLeg", "RightFoot"}
        self.assertLessEqual(required, set(mapped))

    def test_the_humanoid_has_the_three_sockets_and_they_do_not_deform(self):
        sockets = {b.socket: b for b in humanoid().bones if b.socket}
        self.assertEqual({k: v.name for k, v in sockets.items()},
                         {"main_hand": "Socket_MainHand", "off_hand": "Socket_OffHand", "back": "Socket_Back"})
        self.assertFalse(any(b.deform for b in sockets.values()))

    def test_every_socket_names_an_equipment_slot_that_other_players_see(self):
        content = json.loads((REPOSITORY_ROOT / "content" / "characters" / "appearance.json").read_text(encoding="utf-8"))
        slots = {b.socket for b in humanoid().bones if b.socket}
        self.assertLessEqual(slots, set(content["visibleEquipmentSlots"]))

    def test_left_and_right_bones_come_in_pairs(self):
        names = {b.name for b in humanoid().bones}
        for name in names:
            if name.endswith("_L"):
                self.assertIn(name[:-2] + "_R", names)
            if name.endswith("_R"):
                self.assertIn(name[:-2] + "_L", names)

    def test_the_chain_kinds_of_the_code_are_those_of_the_cloth_section(self):
        data = json.loads((SKELETONS / "Humanoid.json").read_text(encoding="utf-8"))
        self.assertEqual({kind["kind"] for kind in data["cloth"]["kinds"]}, set(skeleton.CHAINS))
        used = {b.chain for b in humanoid().bones if b.chain}
        self.assertEqual(used, set(skeleton.CHAINS))

    def test_every_chain_bone_hangs_below_a_bone_of_its_own_chain_or_its_kinds_parent(self):
        loaded = humanoid()
        data = json.loads((SKELETONS / "Humanoid.json").read_text(encoding="utf-8"))
        parents = {kind["kind"]: kind["parent"] for kind in data["cloth"]["kinds"]}
        for bone in loaded.bones:
            if bone.chain:
                parent = loaded.bone(bone.parent)
                self.assertTrue(parent.chain == bone.chain or parent.name == parents[bone.chain], bone.name)


if __name__ == "__main__":
    unittest.main()
