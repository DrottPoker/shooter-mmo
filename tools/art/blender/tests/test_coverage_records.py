"""The data that every garment version exports next to its model, and the reports of its coverage:
coverage.py writes the record, and the committed exports are its output. The tests write the committed
records again and compare the bytes, and check the sentences of the reports. Runs without Blender: see
blender_source.py."""

import ast
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import BLENDER_ROOT, REPOSITORY_ROOT, load  # noqa: E402

garment = load("art_pipeline/garment.py", "version_name")
coverage = load("art_pipeline/coverage.py", "write_record", "Motion", "pair_summary", "motion_summary", "DATA_SCHEMA",
                "REST", "CAGE_REACH", "MAX_LAYERS", garment=garment)
MODELS = REPOSITORY_ROOT / "src" / "Client" / "Assets" / "Art" / "Models" / "Garments"


def write(data: dict) -> str:
    with tempfile.TemporaryDirectory() as folder:
        path = pathlib.Path(folder) / "record.json"
        coverage.write_record(str(path), data)
        return path.read_bytes().decode("utf-8")


class WriteRecordTests(unittest.TestCase):
    def test_numbers_go_sixteen_to_a_line_and_floats_have_five_decimals(self):
        text = write({"schemaVersion": 2, "garment": "Robe", "empty": [], "few": [1, 2.5], "many": list(range(17)), "last": 5})
        self.assertEqual(text, "\n".join([
            "{",
            '    "schemaVersion": 2,',
            '    "garment": "Robe",',
            '    "empty": [],',
            '    "few": [',
            "        1, 2.50000",
            "    ],",
            '    "many": [',
            "        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,",
            "        16",
            "    ],",
            '    "last": 5',
            "}",
            ""]))

    def test_exactly_sixteen_numbers_stay_on_one_line(self):
        text = write({"numbers": list(range(16))})
        self.assertEqual(text.splitlines()[2], "        " + ", ".join(str(i) for i in range(16)))
        self.assertEqual(len(text.splitlines()), 5)

    def test_the_file_uses_unix_line_ends_and_ends_with_one(self):
        raw = write({"a": [1]}).encode("utf-8")
        self.assertNotIn(b"\r", raw)
        self.assertTrue(raw.endswith(b"}\n"))

    def test_what_it_writes_is_json_that_reads_back_as_the_record(self):
        data = {"schemaVersion": 2, "garment": "Robe", "ints": [3, 1, 2], "floats": [0.25, -1.5], "body": "Male"}
        self.assertEqual(json.loads(write(data)), data)

    def test_a_float_is_rounded_to_five_decimals_in_the_file(self):
        self.assertEqual(json.loads(write({"f": [0.123456789]}))["f"], [0.12346])


class CommittedRecordTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.files = sorted(MODELS.glob("*.json"))

    def test_there_are_records_to_check(self):
        self.assertGreaterEqual(len(self.files), 20)

    def test_writing_every_committed_record_again_gives_the_same_bytes(self):
        for file in self.files:
            raw = file.read_bytes().replace(b"\r\n", b"\n")
            with self.subTest(record=file.name):
                self.assertEqual(write(json.loads(raw)).encode("utf-8"), raw)

    def test_every_record_has_the_schema_the_code_writes(self):
        for file in self.files:
            with self.subTest(record=file.name):
                self.assertEqual(json.loads(file.read_text(encoding="utf-8"))["schemaVersion"], coverage.DATA_SCHEMA)

    def test_every_record_has_the_keys_coverage_record_writes_and_no_others(self):
        tree = ast.parse((BLENDER_ROOT / "art_pipeline" / "coverage.py").read_text(encoding="utf-8"))
        record = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "record")
        written = {key.value for node in ast.walk(record) if isinstance(node, ast.Dict) for key in node.keys}
        for file in self.files:
            keys = set(json.loads(file.read_text(encoding="utf-8")))
            with self.subTest(record=file.name):
                # A texture's masks hold the template's data with the texture's name as the garment.
                self.assertEqual(keys, written)

    def test_the_lists_of_a_record_agree_with_its_counts(self):
        for file in self.files:
            data = json.loads(file.read_text(encoding="utf-8"))
            with self.subTest(record=file.name):
                self.assertEqual(len(data["garmentFaceSizes"]), data["garmentFaces"])
                self.assertEqual(sum(data["garmentFaceSizes"]), len(data["garmentFaceVertices"]))
                self.assertEqual(len(data["garmentVertices"]) % 3, 0)
                self.assertLess(max(data["garmentFaceVertices"]), len(data["garmentVertices"]) // 3)
                self.assertEqual(len(data["underlay"]), data["garmentFaces"])
                self.assertTrue(all(0 <= face < data["bodyFaces"] for face in data["underlay"]))
                self.assertTrue(all(0 <= face < data["bodyFaces"] for face in data["covered"]))
                self.assertEqual(data["covered"], sorted(set(data["covered"])))
                self.assertEqual(len(data["pullInVertices"]), len(data["pullInDistances"]))
                self.assertTrue(all(0 <= v < data["bodyVertices"] for v in data["pullInVertices"]))
                self.assertTrue(all(d >= 0.0 for d in data["pullInDistances"]))

    def test_a_record_is_for_the_body_and_garment_its_file_is_named_after(self):
        for file in self.files:
            data = json.loads(file.read_text(encoding="utf-8"))
            with self.subTest(record=file.name):
                self.assertEqual(file.stem, f"{data['garment']}_{data['body']}")

    def test_both_bodies_have_the_same_faces_so_a_garment_s_records_share_their_counts(self):
        by_garment: dict[str, dict] = {}
        for file in self.files:
            data = json.loads(file.read_text(encoding="utf-8"))
            by_garment.setdefault(data["garment"], {})[data["body"]] = data
        for name, bodies in by_garment.items():
            if len(bodies) == 2:
                with self.subTest(garment=name):
                    self.assertEqual(bodies["Male"]["garmentFaceSizes"], bodies["Female"]["garmentFaceSizes"])
                    self.assertEqual(bodies["Male"]["garmentFaceVertices"], bodies["Female"]["garmentFaceVertices"])


class MotionReportTests(unittest.TestCase):
    @staticmethod
    def _motion(**fields):
        values = {"label": "Male", "vertices": 120, "through": 0, "depth": 0.0, "where": "stride", "zone": "Thighs",
                  "checked": 40, **fields}
        return coverage.Motion(**values)

    def test_a_pose_where_nothing_comes_through_says_so(self):
        self.assertEqual(coverage.motion_summary("Shirt", self._motion()),
                         "Shirt_Male: none of the 120 visible vertices under it comes through it in 40 poses and clip frames")

    def test_skin_that_comes_through_is_counted_with_its_depth_and_place(self):
        text = coverage.motion_summary("Shirt", self._motion(through=7, depth=0.0042, where="arms_up", zone="Forearms"))
        self.assertEqual(text, "Shirt_Male: of the 120 visible vertices under it, 7 come through it in 40 poses and clip frames, "
                               "by at most 4.2 mm (Forearms in arms_up), red on the review sheet")

    def test_an_inner_garment_is_reported_under_the_outer_one(self):
        self.assertEqual(coverage.pair_summary("Robe", "Shirt", self._motion(label="Female")),
                         "Shirt under Robe_Female: none of the 120 vertices of it that stay drawn under Robe_Female "
                         "comes through it in 40 poses and clip frames")
        text = coverage.pair_summary("Robe", "Shirt", self._motion(through=3, depth=0.01, zone="Torso", where="sit"))
        self.assertEqual(text, "Shirt under Robe_Male: of the 120 vertices of it that stay drawn under Robe_Male, 3 come "
                               "through it in 40 poses and clip frames, by at most 10.0 mm (Torso in sit)")

    def test_nothing_is_wrong_when_nothing_lies_through_at_rest(self):
        self.assertIsNone(self._motion().rest_error("Shirt_Male", "visible skin vertices"))

    def test_vertices_that_lie_through_at_rest_are_an_error_because_they_show_in_every_pose(self):
        error = self._motion(rest=4).rest_error("Shirt_Male", "visible skin vertices")
        self.assertEqual(error, '4 visible skin vertices lie through "Shirt_Male" at rest, where they show in every pose; '
                                "fix its shape or the cage.")

    def test_the_rest_pose_has_its_own_name_in_the_reports(self):
        self.assertEqual(coverage.REST, "the rest pose")


if __name__ == "__main__":
    unittest.main()
