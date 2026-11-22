"""The content fingerprints that approvals bind to: rounding, order by name, and the chain of the
review stages. Runs without Blender: see blender_source.py."""

import hashlib
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import load  # noqa: E402

core = load("art_pipeline/content_core.py", "VERSION", "STAGES", "PARTS", "Rounded", "fingerprint", "chain", "record",
            "stage_of", "stages_of", numpy=True)
np = __import__("numpy")

SHA = "0" * 64


def parts_of(kind: str, **changed: str) -> dict[str, str]:
    """Every fixed part of a kind with a fingerprint of its name, one clip and one body for the parts
    per item, and the changed parts' fingerprints."""
    found = {}
    for names in core.PARTS[kind].values():
        for name in names:
            name = name + "Idle" if name == "clip:" else name + "Male" if name.endswith(":") else name
            found[name] = hashlib.sha256(name.encode()).hexdigest()
    found.update(changed)
    return found


class RoundingTests(unittest.TestCase):
    def test_negative_zero_is_zero(self):
        self.assertEqual(core.fingerprint([-0.0, 0.0]), core.fingerprint([0.0, -0.0]))
        self.assertEqual(core.fingerprint(np.array([-0.0, -1e-9])), core.fingerprint(np.array([0.0, 0.0])))

    def test_noise_below_the_rounding_changes_nothing(self):
        positions = np.array([[0.1234561, -2.5, 1.0], [3.25, 0.5, -0.75]])
        self.assertEqual(core.fingerprint(positions), core.fingerprint(positions + 2e-8))

    def test_a_change_above_the_rounding_changes_the_fingerprint(self):
        positions = np.array([[0.1234561, -2.5, 1.0], [3.25, 0.5, -0.75]])
        moved = positions.copy()
        # A hundredth of a millimeter.
        moved[0, 0] += 1e-5
        self.assertNotEqual(core.fingerprint(positions), core.fingerprint(moved))

    def test_weights_keep_five_decimals(self):
        weights = np.array([[0.25, 0.75]])
        self.assertEqual(core.fingerprint(core.Rounded(weights, 5)), core.fingerprint(core.Rounded(weights + 3e-7, 5)))
        self.assertNotEqual(core.fingerprint(core.Rounded(weights, 5)), core.fingerprint(core.Rounded(weights + 2e-5, 5)))

    def test_a_float32_array_and_eight_bit_pixels_are_told_apart_from_other_types(self):
        pixels = np.array([0, 128, 255], np.uint8)
        self.assertNotEqual(core.fingerprint(pixels), core.fingerprint(pixels.astype(np.int64)))
        self.assertNotEqual(core.fingerprint(np.array([0.5], np.float32)), core.fingerprint(np.array([0.5])))

    def test_types_and_lengths_are_told_apart(self):
        self.assertNotEqual(core.fingerprint(1), core.fingerprint(1.0))
        self.assertNotEqual(core.fingerprint(True), core.fingerprint(1))
        self.assertNotEqual(core.fingerprint("1"), core.fingerprint(1))
        self.assertNotEqual(core.fingerprint(["ab", "c"]), core.fingerprint(["a", "bc"]))
        self.assertNotEqual(core.fingerprint(None), core.fingerprint(""))


class OrderTests(unittest.TestCase):
    def test_named_things_are_taken_by_name_whatever_their_order(self):
        first = {"Bone_B": {"length": 0.2}, "Bone_A": {"length": 0.1}}
        second = {"Bone_A": {"length": 0.1}, "Bone_B": {"length": 0.2}}
        self.assertEqual(core.fingerprint(first), core.fingerprint(second))

    def test_the_order_of_a_mesh_s_elements_is_content(self):
        self.assertNotEqual(core.fingerprint({"faces": [[0, 1, 2], [2, 3, 0]]}),
                            core.fingerprint({"faces": [[2, 3, 0], [0, 1, 2]]}))


class ChainTests(unittest.TestCase):
    def test_a_stage_hashes_its_kind_name_previous_stage_and_sorted_parts(self):
        parts = parts_of("garment")
        stages = core.chain("garment", parts, ("model", "rig"))
        text = (f"art-content/{core.VERSION}/garment/model\n\n" + "".join(f"{n}={parts[n]}\n" for n in ("materials", "shape", "uv")))
        model = hashlib.sha256(text.encode()).hexdigest()
        rig = hashlib.sha256(f"art-content/{core.VERSION}/garment/rig\n{model}\nweights={parts['weights']}\n".encode()).hexdigest()
        self.assertEqual(stages, {"model": model, "rig": rig})

    def test_a_change_of_the_shape_changes_every_stage(self):
        before = core.chain("skinned", parts_of("skinned"), ("model", "rig", "animation"))
        after = core.chain("skinned", parts_of("skinned", shape=SHA), ("model", "rig", "animation"))
        self.assertTrue(all(before[s] != after[s] for s in before))

    def test_a_change_of_the_weights_keeps_the_model(self):
        before = core.chain("skinned", parts_of("skinned"), ("model", "rig", "animation"))
        after = core.chain("skinned", parts_of("skinned", weights=SHA), ("model", "rig", "animation"))
        self.assertEqual(before["model"], after["model"])
        self.assertNotEqual(before["rig"], after["rig"])
        self.assertNotEqual(before["animation"], after["animation"])

    def test_a_change_of_one_clip_changes_only_the_animation(self):
        stages = ("model", "rig", "animation")
        before = core.chain("skinned", parts_of("skinned"), stages)
        after = core.chain("skinned", parts_of("skinned", **{"clip:Idle": SHA}), stages)
        self.assertEqual((before["model"], before["rig"]), (after["model"], after["rig"]))
        self.assertNotEqual(before["animation"], after["animation"])

    def test_the_kind_is_part_of_every_stage(self):
        parts = {"shape": SHA, "uv": SHA, "materials": SHA}
        self.assertNotEqual(core.chain("static", parts, ("model",))["model"],
                            core.chain("garment", dict(parts, weights=SHA), ("model", "rig"))["model"])


class TableTests(unittest.TestCase):
    def test_every_part_belongs_to_one_stage_of_its_kind(self):
        for kind, stages in core.PARTS.items():
            with self.subTest(kind=kind):
                self.assertEqual(tuple(stages), core.STAGES[kind])
                for stage, names in stages.items():
                    for name in names:
                        item = name + "X" if name.endswith(":") else name
                        self.assertEqual(core.stage_of(kind, item), stage)

    def test_a_skinned_asset_without_clips_ends_with_its_rig(self):
        self.assertEqual(core.stages_of("skinned", has_clips=False), ("model", "rig"))
        self.assertEqual(core.stages_of("skinned"), ("model", "rig", "animation"))

    def test_a_record_lists_its_parts_by_name_and_refuses_a_part_of_no_stage(self):
        record = core.record("static", {"uv": SHA, "shape": SHA, "materials": SHA}, ("model",))
        self.assertEqual(list(record["parts"]), ["materials", "shape", "uv"])
        self.assertEqual(record["version"], core.VERSION)
        with self.assertRaises(ValueError):
            core.record("skinned", parts_of("skinned"), ("model", "rig"))


if __name__ == "__main__":
    unittest.main()
