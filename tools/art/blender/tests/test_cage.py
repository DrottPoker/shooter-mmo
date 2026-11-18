"""The garment cage of cage.py without Blender: its region and zone tables, the rings it builds feet and
heads with, the names it gives, and the reading of its asset.json, checked against art/Cages and the
exported bodies. See blender_source.py."""

import json
import math
import pathlib
import re
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
bodies = load("art_pipeline/bodies.py", "ZONES", "body_name", "part_name", "TOPOLOGY_SCHEMA")
cage = load("art_pipeline/cage.py", "REGIONS", "STAGES", "ATTACHED", "REGION_ZONES", "SKIN_ZONES", "SIDED", "DIGITS",
            "FOOT_RINGS", "HEAD_RINGS", "HEAD_TURN", "OWN_ATTRIBUTES", "version_name", "collection_name", "side_of",
            "digit_of", "read_manifest", "TENSION_STEPS", "LIFT_SHARE", "CLEARANCE", "NEAR", "REACH",
            ArtPipelineError=scene.ArtPipelineError)

ART = REPOSITORY_ROOT / "art"
CAGE_MANIFEST = ART / "Cages" / "Humanoid" / "asset.json"
FINGER_BONE = re.compile(r"^(Thumb|Index|Middle|Ring|Little)(Proximal|Intermediate|Distal)_[LR]$")


class RegionTableTests(unittest.TestCase):
    def test_the_regions_keep_the_order_of_the_face_attribute_the_files_store(self):
        # Every cage and every garment file stores a face's region as its index here.
        self.assertEqual(cage.REGIONS, ("neck", "trunk", "arms", "legs", "hands", "feet", "head"))

    def test_the_body_parts_keep_the_order_of_the_zone_attribute_the_files_store(self):
        self.assertEqual(bodies.ZONES, ("Head", "Neck", "Torso", "Pelvis", "UpperArms", "Forearms", "Hands", "Thighs",
                                        "Shins", "Feet"))

    def test_the_exported_topologies_use_the_zones_the_code_numbers(self):
        for label in ("Male", "Female"):
            data = json.loads((REPOSITORY_ROOT / "src/Client/Assets/Art/Models/Characters" / f"Human_{label}.json")
                              .read_text(encoding="utf-8"))
            with self.subTest(body=label):
                self.assertEqual(data["schemaVersion"], bodies.TOPOLOGY_SCHEMA)
                self.assertEqual(data["body"], label)
                self.assertEqual(set(data["faceZones"]), set(range(len(bodies.ZONES))))
                self.assertEqual(sum(data["faceSizes"]), len(data["faceVertices"]))
                self.assertEqual(len(data["faceZones"]), len(data["faceSizes"]))
                self.assertLess(max(data["faceVertices"]), len(data["vertices"]) // 3)

    def test_the_fitting_stages_name_every_region_once(self):
        flat = [region for stage in cage.STAGES for region in stage]
        self.assertEqual(sorted(flat), sorted(cage.REGIONS))
        self.assertEqual(len(flat), len(set(flat)))

    def test_an_extremity_comes_in_a_later_stage_than_the_region_it_hangs_from(self):
        stage = {region: index for index, regions in enumerate(cage.STAGES) for region in regions}
        for end, parent in cage.ATTACHED.items():
            with self.subTest(end=end):
                self.assertGreater(stage[end], stage[parent])

    def test_the_attached_regions_are_regions_and_a_region_hangs_from_one_that_is_not_attached(self):
        for end, parent in cage.ATTACHED.items():
            self.assertIn(end, cage.REGIONS)
            self.assertIn(parent, cage.REGIONS)
            self.assertNotIn(parent, cage.ATTACHED)

    def test_every_body_part_belongs_to_exactly_one_region(self):
        # paint.py maps each zone to its region with a dict, which would keep only the last of two.
        listed = [zone for zones in cage.REGION_ZONES.values() for zone in zones]
        self.assertEqual(sorted(listed), sorted(bodies.ZONES))
        self.assertEqual(sorted(cage.REGION_ZONES), sorted(cage.REGIONS))

    def test_a_regions_skin_includes_its_own_parts_and_only_known_ones(self):
        self.assertEqual(sorted(cage.SKIN_ZONES), sorted(cage.REGIONS))
        for region, zones in cage.SKIN_ZONES.items():
            with self.subTest(region=region):
                self.assertTrue(set(cage.REGION_ZONES[region]) <= set(zones))
                self.assertTrue(set(zones) <= set(bodies.ZONES))

    def test_the_sided_regions_are_the_limbs_and_their_extremities(self):
        self.assertEqual(set(cage.SIDED), {"arms", "legs", "hands", "feet"})
        self.assertTrue(set(cage.SIDED) <= set(cage.REGIONS))

    def test_the_cage_keeps_its_own_attributes_apart_from_a_garment_s(self):
        self.assertEqual(len(cage.OWN_ATTRIBUTES), len(set(cage.OWN_ATTRIBUTES)))
        self.assertIn("region", cage.OWN_ATTRIBUTES)


class RingTableTests(unittest.TestCase):
    def test_a_foot_turns_in_eight_steps_and_then_reaches_for_its_tip(self):
        turns = [share for kind, share in cage.FOOT_RINGS if kind == "turn"]
        reaches = [share for kind, share in cage.FOOT_RINGS if kind == "reach"]
        self.assertEqual(turns, [step / 8 for step in range(1, 9)])
        self.assertEqual(len(turns) + len(reaches), len(cage.FOOT_RINGS))
        # The turns come first and then the planes across the foot, each kind further along than the last.
        self.assertEqual([kind for kind, _share in cage.FOOT_RINGS], ["turn"] * len(turns) + ["reach"] * len(reaches))
        self.assertEqual(reaches, sorted(reaches))
        self.assertTrue(all(0.0 < share < 1.0 for share in reaches))

    def test_the_head_rings_climb_to_just_short_of_the_crown(self):
        rings = cage.HEAD_RINGS
        self.assertEqual(list(rings), sorted(rings))
        self.assertTrue(0.0 < rings[0] and rings[-1] < 1.0)
        self.assertLess(cage.HEAD_TURN, len(rings))

    def test_the_head_rings_come_closer_together_toward_the_crown(self):
        gaps = [b - a for a, b in zip(cage.HEAD_RINGS, cage.HEAD_RINGS[1:])]
        # Evenly up the neck and the face, then closer over the dome.
        self.assertTrue(all(g > 0 for g in gaps))
        self.assertLess(gaps[-1], gaps[0] / 2)
        for early in gaps[:8]:
            self.assertAlmostEqual(early, gaps[0], delta=0.002)

    def test_the_fitting_constants_are_positive_and_the_lift_a_share(self):
        for name in ("TENSION_STEPS", "CLEARANCE", "NEAR", "REACH"):
            self.assertGreater(getattr(cage, name), 0, name)
        self.assertTrue(0.0 < cage.LIFT_SHARE < 1.0)
        # A vertex takes the nearest point of the skin once it is closer than the reach it looks over.
        self.assertLess(cage.NEAR, cage.REACH)


class CageNameTests(unittest.TestCase):
    def test_the_names_of_a_version_and_its_collection(self):
        self.assertEqual(cage.version_name("Male"), "Cage_Male")
        self.assertEqual(cage.collection_name("Female"), "Fit_Female")

    def test_a_body_and_its_parts_are_named_after_their_armature(self):
        self.assertEqual(bodies.body_name("Male"), "Male_Body")
        self.assertEqual(bodies.part_name("Male", "UpperArms"), "Male_UpperArms")

    def test_the_left_side_lies_at_negative_x_because_bodies_face_plus_y(self):
        self.assertEqual(cage.side_of(-0.001), "L")
        self.assertEqual(cage.side_of(-5.0), "L")
        self.assertEqual(cage.side_of(0.001), "R")
        # On the centre line a face counts to the right.
        self.assertEqual(cage.side_of(0.0), "R")

    def test_a_digit_is_one_plus_its_place_and_nothing_else_has_one(self):
        self.assertEqual([cage.digit_of(f"{name}Proximal_R") for name in cage.DIGITS], [1, 2, 3, 4, 5])
        self.assertEqual(cage.digit_of("Hand_L"), 0)
        self.assertEqual(cage.digit_of("Hips"), 0)
        self.assertEqual(cage.digit_of(""), 0)

    def test_the_digits_of_the_skeleton_are_exactly_its_finger_bones(self):
        data = json.loads((ART / "Skeletons" / "Humanoid.json").read_text(encoding="utf-8"))
        for bone in data["bones"]:
            with self.subTest(bone=bone["name"]):
                self.assertEqual(cage.digit_of(bone["name"]) > 0, bool(FINGER_BONE.match(bone["name"])))


class CageManifestTests(unittest.TestCase):
    def _read(self, data: dict):
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "asset.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return cage.read_manifest(str(path))

    def test_the_figure_scale_and_the_lines_are_read_with_their_types(self):
        figure, lines = self._read({"figure": "0.06", "lines": [
            {"name": "Neckline", "region": "neck", "bone": "Neck", "share": "0.19", "rise": 5},
            {"name": "Waist", "region": "trunk", "bone": "Spine", "share": 0.1, "ignored": True}]})
        self.assertEqual(figure, 0.06)
        self.assertEqual(lines, [{"name": "Neckline", "region": "neck", "bone": "Neck", "share": 0.19, "rise": 5.0},
                                 {"name": "Waist", "region": "trunk", "bone": "Spine", "share": 0.1}])

    def test_a_figure_scale_must_be_a_positive_number_of_meters(self):
        for figure in (0, -0.06, float("nan"), float("inf")):
            with self.subTest(figure=figure), self.assertRaisesRegex(scene.ArtPipelineError, "positive number"):
                self._read({"figure": figure, "lines": []})

    def test_the_lines_of_the_real_cage_are_whole_and_unique(self):
        figure, lines = cage.read_manifest(str(CAGE_MANIFEST))
        self.assertEqual(figure, 0.06)
        names = [line["name"] for line in lines]
        self.assertEqual(len(names), len(set(names)))
        for line in lines:
            with self.subTest(line=line["name"]):
                self.assertIn(line["region"], cage.REGIONS)
                self.assertTrue(0.0 < line["share"] < 1.0)
                self.assertTrue(math.isfinite(line.get("rise", 0.0)))

    def test_every_line_stands_on_a_bone_of_the_skeleton_on_each_side_of_its_region(self):
        bones = {b["name"] for b in json.loads((ART / "Skeletons" / "Humanoid.json").read_text(encoding="utf-8"))["bones"]}
        _figure, lines = cage.read_manifest(str(CAGE_MANIFEST))
        for line in lines:
            names = [f"{line['bone']}_{side}" for side in ("L", "R")] if line["region"] in cage.SIDED else [line["bone"]]
            with self.subTest(line=line["name"]):
                self.assertTrue(set(names) <= bones, names)


if __name__ == "__main__":
    unittest.main()
