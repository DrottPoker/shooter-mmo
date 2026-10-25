"""The art pipeline's kinematic cloth lays the chains as the Unity client does: every case of the
fixtures that ClothKinematicFixtures writes from the Unity bodies is laid again with cloth_core.py and
compared with what ClothPose laid. Runs without Blender:
python -m unittest discover -s tools/blender/tests. See docs/ART_PIPELINE.md."""

import json
import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from art_pipeline import cloth_core  # noqa: E402

FIXTURES = pathlib.Path(__file__).resolve().parent / "fixtures" / "cloth_kinematics.json"
# Unity lays in single precision and this in double: the cases may differ by this much, in meters.
TOLERANCE = 1e-5


def _distance(a, b) -> float:
    return math.dist(a, b)


class ClothCoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(FIXTURES, encoding="utf-8") as file:
            cls.fixture = json.load(file)

    def test_every_case_lays_as_unity_laid_it(self):
        self.assertEqual(self.fixture["version"], 2)
        self.assertTrue(self.fixture["cases"])
        worst = (0.0, "")
        for case in self.fixture["cases"]:
            laid = cloth_core.lay(case)
            for chain, positions in zip(case["chains"], laid):
                expected = [cloth_core.vector(point) for point in chain["expected"]]
                self.assertEqual(len(positions), len(expected), f"{case['name']} {chain['root']}")
                for index, (point, wanted) in enumerate(zip(positions, expected)):
                    distance = _distance(point, wanted)
                    worst = max(worst, (distance, f"{case['name']} {chain['root']} particle {index}"))
        self.assertLessEqual(worst[0], TOLERANCE, f"farthest from Unity: {worst[1]} by {worst[0] * 1000:.3f} mm")

    def test_a_body_at_rest_wears_its_cloth_as_made(self):
        # In the bind pose nothing swings a chain away from where it hangs, but a cloak over a robe
        # lies over the robe's skirt.
        for case in self.fixture["cases"]:
            if not case["name"].endswith("/rest"):
                continue
            worn = {chain["kind"] for chain in case["chains"] if chain["reachKnown"]}
            for chain, positions in zip(case["chains"], cloth_core.lay(case)):
                if chain["reachKnown"] and cloth_core.KINDS[chain["kind"]]["over"] in worn:
                    continue
                for index, (point, hanging) in enumerate(zip(positions, chain["hanging"])):
                    self.assertLessEqual(_distance(point, cloth_core.vector(hanging)), 1e-3,
                                         f"{case['name']} {chain['root']} particle {index}")

    def test_the_constants_name_every_chain_of_the_skeleton(self):
        with open(cloth_core.SKELETON, encoding="utf-8") as file:
            skeleton = json.load(file)
        chained = {bone["name"] for bone in skeleton["bones"] if bone.get("chain")}
        named = set()
        for kind in cloth_core.CLOTH["kinds"]:
            for chain in kind["chains"]:
                for suffix in (("_L", "_R") if chain["sided"] else ("",)):
                    named.update(f"{chain['name']}{number}{suffix}" for number in range(1, kind["bones"] + 1))
        self.assertEqual(named, chained)


if __name__ == "__main__":
    unittest.main()
