"""The stiff parts of a modeled garment template, stiff.py: the islands of a mesh, the parts an asset.json
names and the mistakes that stop a sync, and the similarity a part follows a move with, for a part that
stays, a pure turn, a scale, and points with noise. Runs without Blender: see blender_source.py. It needs
numpy."""

import json
import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

scene = load("art_pipeline/scene.py", "ArtPipelineError")
stiff = load("art_pipeline/stiff.py", "FIELD", "entries", "islands", "parts", "similarity", "carry", "measures", numpy=True,
             ArtPipelineError=scene.ArtPipelineError)

import numpy as np  # noqa: E402  (load() skips the file without numpy)

# A strip of two quads, a triangle of its own, a quad of its own, and a vertex of no face.
MESH = [(0, 1, 4, 3), (1, 2, 5, 4), (6, 7, 8), (9, 10, 11, 12)]
COUNT = 14


def turn(axis, degrees: float) -> np.ndarray:
    """The rotation by an angle about an axis, by Rodrigues' formula."""
    x, y, z = np.asarray(axis, float) / np.linalg.norm(axis)
    k = np.array([[0.0, -z, y], [z, 0.0, -x], [-y, x, 0.0]])
    angle = math.radians(degrees)
    return np.eye(3) + math.sin(angle) * k + (1.0 - math.cos(angle)) * k @ k


# A thin bent bar of 12 points, about 9 cm long, as a nasal stands off a face.
BAR = np.array([[0.004 * (i % 2), 0.18 + 0.002 * i, 1.72 - 0.008 * i] for i in range(12)])


class IslandTests(unittest.TestCase):
    def test_vertices_joined_by_edges_share_an_island_numbered_by_its_lowest_vertex(self):
        self.assertEqual(stiff.islands(COUNT, MESH).tolist(), [0, 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3])

    def test_the_order_of_the_faces_does_not_change_the_numbers(self):
        self.assertEqual(stiff.islands(COUNT, list(reversed(MESH))).tolist(), stiff.islands(COUNT, MESH).tolist())

    def test_a_part_is_the_island_of_its_vertex_on_every_version(self):
        found = stiff.parts([("bar", 8), ("plate", 10)], COUNT, MESH, "Helm_Male")
        self.assertEqual([(name, members.tolist()) for name, members in found], [("bar", [6, 7, 8]), ("plate", [9, 10, 11, 12])])

    def test_no_part_named_is_none(self):
        self.assertEqual(stiff.parts([], COUNT, MESH, "Helm_Male"), [])

    def test_a_vertex_out_of_range_names_the_field_the_part_and_the_count(self):
        with self.assertRaises(scene.ArtPipelineError) as raised:
            stiff.parts([("bar", COUNT)], COUNT, MESH, "Helm_Male")
        for text in ('"stiff"', '"bar"', f"vertex {COUNT}", f"{COUNT} vertices", '"Helm_Male"'):
            self.assertIn(text, str(raised.exception))
        with self.assertRaises(scene.ArtPipelineError):
            stiff.parts([("bar", -1)], COUNT, MESH, "Helm_Male")

    def test_an_island_that_is_the_whole_mesh_is_no_stiff_part(self):
        with self.assertRaises(scene.ArtPipelineError) as raised:
            stiff.parts([("all", 0)], 6, MESH[:2], "Helm_Male")
        self.assertIn('"stiff"', str(raised.exception))
        self.assertIn("whole mesh", str(raised.exception))

    def test_two_parts_on_one_island_stop_the_sync(self):
        with self.assertRaises(scene.ArtPipelineError) as raised:
            stiff.parts([("bar", 6), ("tip", 8)], COUNT, MESH, "Helm_Male")
        self.assertIn('"stiff"', str(raised.exception))
        self.assertIn('"bar" and "tip"', str(raised.exception))


class EntryTests(unittest.TestCase):
    def test_the_parts_are_read_in_order_and_none_without_the_field(self):
        self.assertEqual(stiff.entries({"stiff": [{"name": "nasal", "vertex": 3}, {"name": "cheek", "vertex": 9}]}),
                         [("nasal", 3), ("cheek", 9)])
        self.assertEqual(stiff.entries({"recipe": "modeled"}), [])
        self.assertEqual(stiff.entries({"stiff": []}), [])

    def test_a_field_of_another_form_is_refused(self):
        for wrong in ({"name": "nasal", "vertex": 3}, [{"name": "nasal"}], [{"name": "nasal", "vertex": 1.5}],
                      [{"name": "nasal", "vertex": True}], ["nasal"]):
            with self.subTest(wrong=wrong):
                with self.assertRaises(scene.ArtPipelineError):
                    stiff.entries({"stiff": wrong})

    def test_the_helmet_names_its_nasal_and_only_modeled_templates_name_parts(self):
        for folder in sorted((REPOSITORY_ROOT / "art" / "Garments").iterdir()):
            manifest = json.loads((folder / "asset.json").read_text(encoding="utf-8"))
            with self.subTest(garment=folder.name):
                named = stiff.entries(manifest)
                self.assertTrue(not named or manifest["recipe"] == "modeled")
                if folder.name == "NasalHelm":
                    self.assertEqual([name for name, _vertex in named], ["nasal"])


class SimilarityTests(unittest.TestCase):
    def assertCarries(self, transform, source, target, tolerance: float) -> None:
        self.assertLess(np.abs(stiff.carry(transform, source) - target).max(), tolerance)

    def test_a_part_that_did_not_move_has_the_identity(self):
        rotation, scale, translation = transform = stiff.similarity(BAR, BAR)
        self.assertLess(np.abs(rotation - np.eye(3)).max(), 1e-12)
        self.assertAlmostEqual(scale, 1.0, places=12)
        self.assertLess(np.abs(translation).max(), 1e-12)
        self.assertCarries(transform, BAR, BAR, 1e-12)
        centre, farthest, degrees, factor = stiff.measures(transform, BAR)
        self.assertLess(max(centre, farthest, degrees), 1e-6)
        self.assertAlmostEqual(factor, 1.0, places=12)

    def test_a_pure_turn_and_shift_is_found_and_measured(self):
        rotation = turn((0.3, -1.0, 0.2), 8.0)
        moved = BAR @ rotation.T + np.array([0.001, 0.006, -0.004])
        found, scale, _translation = transform = stiff.similarity(BAR, moved)
        self.assertLess(np.abs(found - rotation).max(), 1e-9)
        self.assertAlmostEqual(scale, 1.0, places=9)
        self.assertCarries(transform, BAR, moved, 1e-9)
        _centre, _farthest, degrees, _factor = stiff.measures(transform, BAR)
        self.assertAlmostEqual(degrees, 8.0, places=6)

    def test_a_scale_about_a_point_is_one_scale(self):
        centre = np.array([0.0, 0.05, 1.68])
        moved = centre + 1.04 * (BAR - centre)
        rotation, scale, _translation = transform = stiff.similarity(BAR, moved)
        self.assertAlmostEqual(scale, 1.04, places=9)
        self.assertLess(np.abs(rotation - np.eye(3)).max(), 1e-9)
        self.assertCarries(transform, BAR, moved, 1e-9)
        shift, _farthest, _degrees, factor = stiff.measures(transform, BAR)
        self.assertAlmostEqual(factor, 1.04, places=9)
        self.assertAlmostEqual(shift, 0.04 * np.linalg.norm(BAR.mean(axis=0) - centre), places=9)

    def test_noise_moves_the_fit_little_and_keeps_the_part_s_shape(self):
        rotation = turn((1.0, 0.2, 0.1), 3.0)
        exact = 0.98 * BAR @ rotation.T + np.array([0.0, 0.003, -0.002])
        noisy = exact + np.random.default_rng(7).normal(0.0, 0.0001, BAR.shape)
        transform = stiff.similarity(BAR, noisy)
        # A thin bar's turn about its length is weakly held, so the fit is measured where it places the
        # part: within a few times the noise of where the exact move puts it.
        self.assertAlmostEqual(transform[1], 0.98, delta=0.005)
        self.assertCarries(transform, BAR, exact, 0.0003)
        # Carried as one piece, the part keeps its shape up to the one scale, whatever the noise.
        carried = stiff.carry(transform, BAR)
        before = np.linalg.norm(BAR[:, None] - BAR[None], axis=2)
        after = np.linalg.norm(carried[:, None] - carried[None], axis=2)
        ratios = after[before > 0.0] / before[before > 0.0]
        self.assertLess(ratios.max() - ratios.min(), 1e-12)

    def test_a_mirror_image_is_never_taken_for_a_turn(self):
        mirrored = BAR * np.array([-1.0, 1.0, 1.0])
        rotation, scale, _translation = stiff.similarity(BAR, mirrored)
        self.assertAlmostEqual(float(np.linalg.det(rotation)), 1.0, places=9)
        self.assertGreater(scale, 0.0)

    def test_one_point_only_moves(self):
        rotation, scale, translation = stiff.similarity(BAR[:1], BAR[:1] + 0.002)
        self.assertEqual((rotation.tolist(), scale), (np.eye(3).tolist(), 1.0))
        self.assertLess(np.abs(translation - 0.002).max(), 1e-12)


if __name__ == "__main__":
    unittest.main()
