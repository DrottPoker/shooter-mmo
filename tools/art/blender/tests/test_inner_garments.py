"""The logic that keeps a garment worn under another one from showing through it, without Blender: the
split of every quad across the diagonal it folds outward across, alike on both sides of the body; the
hide margin from the open edges of the outer garment; the faces of a turned edge that go with their
cloth; the places where the pair check follows a drawn triangle; and a texture whose template only
turned the corners of its faces. Small fixtures whose answer follows from the input: two quads that
fold opposite ways, a face under a hem, a face at the margin. It needs numpy; see blender_source.py."""

import json
import pathlib
import sys
import types
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import REPOSITORY_ROOT, load  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

garment = load("art_pipeline/garment.py", "quad_bend", "mirror_faces", "split_starts", "mirror_split_faces",
               "mirror_split_error", "recipe_text", "FLAT_BEND", "MIRROR_MATCH", "SPLIT_SNAP", "OVER_GAP", "turned_edges",
               numpy=True)
coverage = load("art_pipeline/coverage.py", "margin_slack", "edge_margins", "beyond_margin", "turned_follow",
                "triangle_samples", "overlay_weights", "HIDE_MARGIN", "HIDE_ROUNDING", "TRIANGLE_SPACING", "DEPTH_ROUNDING",
                "EDGE_GAPS", "removed_under", "drawn_triangles", numpy=True)
texture = load("art_pipeline/texture.py", "same_corners", "refines", "in_uv_face", "places_match", numpy=True)

ART = REPOSITORY_ROOT / "art"


def ridge(height: float, x: float = 0.0) -> np.ndarray:
    """A unit quad in the plane z = 0, facing up, wound counterclockwise seen from above, with its first
    and third corners raised by the height: across its first diagonal it folds as a ridge for a
    positive height and as a valley for a negative one."""
    return np.array([(x, 0.0, height), (x + 1.0, 0.0, 0.0), (x + 1.0, 1.0, height), (x, 1.0, 0.0)])


def mirrored(points: np.ndarray, polygon: tuple[int, ...]) -> tuple[np.ndarray, tuple[int, ...]]:
    """The mirror image of a face across x = 0: its points mirrored, and its corners wound the other way
    round, so it still faces out, starting from the image of the given corner."""
    return points * np.array([-1.0, 1.0, 1.0]), tuple(reversed(polygon))


class QuadBendTests(unittest.TestCase):
    def test_two_quads_that_fold_opposite_ways_bend_by_opposite_amounts(self):
        self.assertAlmostEqual(garment.quad_bend(ridge(0.003)), 0.003)
        self.assertAlmostEqual(garment.quad_bend(ridge(-0.003)), -0.003)

    def test_a_flat_quad_bends_by_nothing_and_so_does_a_degenerate_one(self):
        self.assertEqual(garment.quad_bend(ridge(0.0)), 0.0)
        self.assertEqual(garment.quad_bend(np.zeros((4, 3))), 0.0)

    def test_the_bend_across_the_other_diagonal_is_the_opposite(self):
        quad = ridge(0.002)
        self.assertAlmostEqual(garment.quad_bend(quad[[1, 2, 3, 0]]), -garment.quad_bend(quad))


class SplitStartTests(unittest.TestCase):
    def split(self, points: np.ndarray) -> list[int]:
        return garment.split_starts(points, [tuple(range(len(points)))])

    def test_a_quad_splits_across_the_diagonal_it_folds_outward_across(self):
        self.assertEqual(self.split(ridge(0.003)), [0])
        self.assertEqual(self.split(ridge(-0.003)), [1])

    def test_a_flat_quad_takes_its_shorter_diagonal(self):
        kite = np.array([(0.0, 0.0, 0.0), (2.0, 0.0, 0.0), (2.2, 0.2, 0.0), (0.0, 0.5, 0.0)])
        lengths = (np.linalg.norm(kite[2] - kite[0]), np.linalg.norm(kite[3] - kite[1]))
        self.assertEqual(self.split(kite), [0 if lengths[0] < lengths[1] else 1])
        # A bend within the flatness counts as flat.
        nearly = kite + np.array([(0.0, 0.0, garment.FLAT_BEND / 4), (0.0, 0.0, 0.0)] * 2)
        self.assertEqual(self.split(nearly), self.split(kite))

    def test_a_flat_square_is_decided_by_its_geometry_not_its_first_corner(self):
        square = np.array([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 0.0, 1.0), (0.0, 0.0, 1.0)])
        # Both diagonals are as steep; the one reaching farther from the middle plane wins, whichever
        # corner the face starts from.
        first = garment.split_starts(square, [(0, 1, 2, 3)])
        turned = garment.split_starts(square, [(1, 2, 3, 0)])
        chosen = [{0, 2}, {1, 3}][first[0]]
        chosen_turned = [{1, 3}, {2, 0}][turned[0]]
        self.assertEqual(chosen, chosen_turned)

    def test_a_triangle_and_an_ngon_keep_their_first_corner(self):
        points = np.array([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.1), (0.5, 1.5, 0.0), (0.0, 1.0, 0.2)])
        self.assertEqual(garment.split_starts(points, [(0, 1, 2), (0, 1, 2, 3, 4)]), [0, 0])

    def test_a_quad_and_its_mirror_image_split_along_mirrored_diagonals(self):
        # The quad on the right side starts at its first corner; its image on the left side is wound
        # the other way and starts at the image of another corner, as a cage's mirrored faces do.
        right = ridge(0.002, x=0.5)
        left, wound = mirrored(right, (0, 1, 2, 3))
        points = np.vstack([right, left])
        image = tuple(4 + v for v in wound[1:] + wound[:1])
        polygons = [(0, 1, 2, 3), image]
        starts = garment.split_starts(points, polygons)
        diagonals = [{polygons[f][starts[f]], polygons[f][(starts[f] + 2) % 4]} for f in range(2)]
        self.assertEqual({v - 4 for v in diagonals[1]}, diagonals[0])
        turned = [p[s:] + p[:s] for p, s in zip(polygons, starts)]
        self.assertEqual(garment.mirror_split_faces(points, turned), (1, []))

    def test_a_flat_pair_decides_alike_although_float_noise_bends_its_halves_opposite_ways(self):
        right = ridge(0.0, x=0.5) + np.array([(0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 1e-7), (0.0, 0.0, 0.0)])
        left, wound = mirrored(ridge(0.0, x=0.5) - np.array([(0.0, 0.0, 0.0)] * 2 + [(0.0, 0.0, 1e-7), (0.0, 0.0, 0.0)]),
                               (0, 1, 2, 3))
        points = np.vstack([right, left])
        polygons = [(0, 1, 2, 3), tuple(4 + v for v in wound)]
        starts = garment.split_starts(points, polygons)
        turned = [p[s:] + p[:s] for p, s in zip(polygons, starts)]
        self.assertEqual(garment.mirror_split_faces(points, turned), (1, []))

    def test_the_mirror_rule_names_the_pairs_that_split_differently(self):
        right = ridge(0.002, x=0.5)
        left, wound = mirrored(right, (0, 1, 2, 3))
        points = np.vstack([right, left])
        same = (0, 1, 2, 3), tuple(4 + v for v in wound[1:] + wound[:1])
        crossed = (0, 1, 2, 3), tuple(4 + v for v in wound)
        good = same if garment.mirror_split_faces(points, list(same))[1] == [] else crossed
        bad = crossed if good is same else same
        self.assertEqual(garment.mirror_split_faces(points, list(good)), (1, []))
        self.assertEqual(garment.mirror_split_faces(points, list(bad)), (1, [0]))
        self.assertIsNone(garment.mirror_split_error("Shirt_Male", points, list(good)))
        error = garment.mirror_split_error("Shirt_Male", points, list(bad))
        self.assertTrue(error.startswith('"Shirt_Male" splits 1 of its 1 pairs of mirrored quads'))
        self.assertIn("such as the face 0 at (1.000, 0.500, 0.001) m", error)
        self.assertIn("-Rebuild", error)

    def test_a_quad_that_is_its_own_mirror_image_is_no_pair(self):
        # A quad across the middle plane, its corners mirrored onto each other: either split of it
        # mirrors to the other, so it is left out of the rule, and so is one along the middle.
        across = np.array([(-0.5, 0.0, 0.0), (0.5, 0.0, 0.0), (0.4, 0.0, 1.0), (-0.4, 0.0, 1.0)])
        self.assertEqual(garment.mirror_faces(across, [(0, 1, 2, 3)])[0][0], 0)
        for start in range(4):
            with self.subTest(start=start):
                polygon = tuple((k + start) % 4 for k in range(4))
                self.assertEqual(garment.mirror_split_faces(across, [polygon]), (0, []))
                self.assertIsNone(garment.mirror_split_error("Robe_Male", across, [polygon]))
        kite = np.array([(0.0, 0.0, 0.0), (0.3, 0.0, 0.5), (0.0, 0.0, 1.0), (-0.3, 0.0, 0.5)])
        self.assertEqual(garment.mirror_split_faces(kite, [(0, 1, 2, 3)]), (0, []))

    def test_faces_find_their_images_within_the_match_and_no_farther(self):
        right = ridge(0.002, x=0.5)
        left, wound = mirrored(right, (0, 1, 2, 3))
        off = left + np.array([0.0, 0.0, 2.0 * garment.MIRROR_MATCH])
        found = garment.mirror_faces(np.vstack([right, left]), [(0, 1, 2, 3), tuple(4 + v for v in wound)])
        self.assertEqual(found[0][0], 1)
        self.assertEqual(found[1][0], 0)
        self.assertEqual(garment.mirror_faces(np.vstack([right, off]), [(0, 1, 2, 3), tuple(4 + v for v in wound)]),
                         [None, None])


class SplitRecipeTests(unittest.TestCase):
    BASE = {"regions": ["trunk"], "cuts": [{"line": "Waist", "keep": "head"}], "ease": 0, "hem": 0.002}

    def test_the_offset_of_a_split_is_kept_as_a_float_and_an_empty_list_reads_like_none(self):
        text = garment.recipe_text({**self.BASE, "splits": [{"line": "HemHip"}, {"line": "HemHip", "toward": "tail", "offset": 1}]})
        self.assertIn('"splits":[{"line":"HemHip"},{"line":"HemHip","offset":1.0,"toward":"tail"}]', text)
        self.assertEqual(garment.recipe_text({**self.BASE, "splits": []}), garment.recipe_text(self.BASE))

    def test_every_recipe_splits_inner_templates_just_beyond_the_hide_margin_inside_where_an_outer_one_ends(self):
        cage = {line["name"]: line for line in json.loads((ART / "Cages" / "Humanoid" / "asset.json").read_text(encoding="utf-8"))["lines"]}
        found = {}
        for folder in sorted((ART / "Garments").iterdir()):
            recipe = json.loads((folder / "asset.json").read_text(encoding="utf-8"))["recipe"]
            for split in recipe.get("splits", []) if isinstance(recipe, dict) else []:
                with self.subTest(garment=folder.name, split=split):
                    self.assertIn(cage[split["line"]]["region"], recipe["regions"])
                    if "offset" in split:
                        self.assertGreaterEqual(split["offset"], coverage.HIDE_MARGIN)
                        self.assertIn(split["toward"], ("head", "tail"))
                found.setdefault(folder.name, []).append((split["line"], split.get("toward"), split.get("offset")))
        # Inside a top's hem toward the head, inside a boot's shaft toward the foot, and inside a glove
        # toward the hand: each where the outer template keeps its line's side, just beyond the margin
        # its open edge there keeps on the first body, rounded up to a millimeter. A top's hem keeps a
        # margin of 10 mm at the back up to 16.5 mm at the front of the hip, so the trousers get loops
        # just beyond the least margin, 11 mm, just beyond the largest, 17 mm, and at 13 and 15 mm
        # between, where the trousers come through the back of a top in motion, so the faces there go
        # as far as the margin around them allows; one more at 19 mm keeps the top, which is fitted
        # over the trousers, closer to them there, so they come through it least; a boot's rim stands
        # about 10 mm off them, a margin of 41.0 mm that reaches 39.5 mm up the shaft; a glove's cuff
        # 2 mm off a sleeve, the least margin of 10 mm, which reaches 10.1 mm into it.
        self.assertEqual(found["Trousers"], [("HemHip", None, None), ("HemHip", "tail", 0.011), ("HemHip", "tail", 0.013),
                                             ("HemHip", "tail", 0.015), ("HemHip", "tail", 0.017), ("HemHip", "tail", 0.019),
                                             ("BootCalf", None, None), ("BootCalf", "tail", 0.04)])
        self.assertEqual(found["LongShirt"], [("GloveCuff", None, None), ("GloveCuff", "tail", 0.011)])
        self.assertEqual(found["Robe"], [("GloveCuff", None, None), ("GloveCuff", "tail", 0.011)])
        keeps = {folder.name: {cut["line"]: cut["keep"] for cut in json.loads((folder / "asset.json").read_text(encoding="utf-8"))["recipe"]["cuts"]}
                 for folder in (ART / "Garments" / name for name in ("Shirt", "Boots", "Gloves"))}
        self.assertEqual((keeps["Shirt"]["HemHip"], keeps["Boots"]["BootCalf"], keeps["Gloves"]["GloveCuff"]), ("tail",) * 3)

    def test_the_rounding_of_the_margin_is_how_far_a_split_may_run_off_its_plane(self):
        self.assertEqual(coverage.HIDE_ROUNDING, garment.SPLIT_SNAP)
        self.assertLess(coverage.HIDE_ROUNDING, coverage.HIDE_MARGIN)
        self.assertGreater(garment.OVER_GAP, coverage.DEPTH_ROUNDING)


class HideMarginTests(unittest.TestCase):
    # The lip of an outer template's hem along x at y = z = 0, as two segments.
    LIP = np.array([[(-0.1, 0.0, 0.0), (0.0, 0.0, 0.0)], [(0.0, 0.0, 0.0), (0.1, 0.0, 0.0)]])
    NONE = np.empty((0, 3))
    NO_SEGMENTS = np.empty((0, 2, 3))

    @staticmethod
    def quad(low: float, high: float, depth: float = 0.003) -> np.ndarray:
        """A quad of the inner template, 20 mm wide, from height low to height high above the lip,
        the depth under the outer cloth."""
        return np.array([(-0.01, depth, low), (0.01, depth, low), (0.01, depth, high), (-0.01, depth, high)])

    def slack(self, face: np.ndarray, margin: float = coverage.HIDE_MARGIN) -> float:
        return coverage.margin_slack(face, self.LIP, [margin] * len(self.LIP), self.NONE, [])

    def test_the_margin_of_a_place_is_four_times_its_gap_and_never_less_than_the_hide_margin(self):
        self.assertEqual(coverage.EDGE_GAPS, 4.0)
        np.testing.assert_allclose(coverage.edge_margins([0.0, 0.002, 0.0025, 0.0105]), [0.01, 0.01, 0.01, 0.042])

    def test_a_face_lies_beyond_the_margins_by_its_distance_to_the_nearest_place_less_that_place_s_margin(self):
        self.assertAlmostEqual(self.slack(self.quad(0.005, 0.05)), np.hypot(0.003, 0.005) - coverage.HIDE_MARGIN)
        # Beyond the end of the lip, to its end.
        beside = self.quad(0.0, 0.01) + np.array([0.115, 0.0, 0.0])
        self.assertAlmostEqual(self.slack(beside), np.hypot(0.005, 0.003) - coverage.HIDE_MARGIN)
        # A face farther from every place than its margin lies beyond them all, by however much.
        self.assertGreater(self.slack(self.quad(0.02, 0.05)), 0.0)

    def test_a_place_with_a_wider_margin_decides_though_another_lies_nearer(self):
        # The left half of the lip lies on the inner template, the right half 10 mm off it, as a boot's
        # rim stands off the trousers: a face 30 mm inside is 30 mm from both and within the right's
        # 40 mm margin, though beyond the left's 10 mm.
        face = self.quad(0.03, 0.05) - np.array([0.011, 0.0, 0.0])
        slack = coverage.margin_slack(face, self.LIP, coverage.edge_margins([0.0, 0.01]), self.NONE, [])
        # The face's corner nearest the right half lies 1 mm left of its end.
        self.assertAlmostEqual(slack, np.hypot(np.hypot(0.001, 0.003), 0.03) - 0.04)
        self.assertFalse(coverage.beyond_margin(slack))
        self.assertTrue(coverage.beyond_margin(coverage.margin_slack(face, self.LIP, coverage.edge_margins([0.0, 0.0]),
                                                                     self.NONE, [])))

    def test_an_edge_that_crosses_a_face_between_its_corners_lies_on_it(self):
        # The corners lie 10 mm below and 40 mm above the lip; the lip runs across the face.
        self.assertAlmostEqual(self.slack(self.quad(-0.01, 0.04)), 0.003 - coverage.HIDE_MARGIN)

    def test_a_line_where_a_texture_is_cut_counts_as_an_edge_also_over_the_inside_of_a_face(self):
        cut = np.array([(0.0, 0.0, 0.03)])
        self.assertAlmostEqual(coverage.margin_slack(self.quad(0.02, 0.05), self.NO_SEGMENTS, [], cut, [0.01]), 0.003 - 0.01)
        self.assertAlmostEqual(coverage.margin_slack(self.quad(0.035, 0.05), self.NO_SEGMENTS, [], cut, [0.02]),
                               np.hypot(0.003, 0.005) - 0.02)
        self.assertEqual(coverage.margin_slack(self.quad(0.0, 0.01), self.NO_SEGMENTS, [], self.NONE, []), np.inf)

    def test_a_place_farther_than_its_margin_from_the_box_around_a_face_is_left_out(self):
        cut = np.array([(0.0, 0.0, 0.2)])
        self.assertEqual(coverage.margin_slack(self.quad(0.0, 0.01), self.NO_SEGMENTS, [], cut, [0.05]), np.inf)
        self.assertLess(coverage.margin_slack(self.quad(0.0, 0.01), self.NO_SEGMENTS, [], cut, [0.25]), 0.0)

    def face_beyond(self, low: float, high: float) -> bool:
        return coverage.beyond_margin(self.slack(self.quad(low, high)))

    def test_a_face_under_a_hem_well_inside_the_outer_garment_lies_beyond_the_margin(self):
        self.assertTrue(self.face_beyond(0.03, 0.06))

    def test_a_face_at_the_hem_lies_within_the_margin(self):
        self.assertFalse(self.face_beyond(0.0, coverage.HIDE_MARGIN))
        self.assertFalse(self.face_beyond(-0.01, 0.04))

    def test_a_face_from_a_split_at_the_margin_lies_beyond_it_where_the_split_runs_off_its_plane(self):
        # Its lower corners lie on a split at the margin: by their straight distance they lie farther,
        # and a split that ran through a vertex up to its rounding inside still counts.
        self.assertTrue(self.face_beyond(coverage.HIDE_MARGIN, 0.03))
        self.assertTrue(self.face_beyond(np.sqrt((coverage.HIDE_MARGIN - coverage.HIDE_ROUNDING) ** 2 - 0.003 ** 2) + 1e-9, 0.03))
        self.assertFalse(self.face_beyond(coverage.HIDE_MARGIN - 2.0 * coverage.HIDE_ROUNDING, 0.03))


class TurnedFollowTests(unittest.TestCase):
    # Two cloth quads along a hem, the vertices 0 to 5, and the turned edge of their open bottom
    # edge 0, 1, 2: rims 6, 8, 10 and flaps 7, 9, 11.
    CLOTH = [(0, 1, 4, 3), (1, 2, 5, 4)]
    TURNED = [(0, 6, 7), (1, 8, 9), (2, 10, 11)]
    FACES = CLOTH + [(1, 0, 6, 8), (8, 6, 7, 9), (2, 1, 8, 10), (10, 8, 9, 11)]

    def test_a_rim_and_its_flap_go_with_the_cloth_face_they_are_turned_from(self):
        hidden = np.array([True, False, False, False, True, True])
        self.assertEqual(coverage.turned_follow(self.FACES, self.TURNED, hidden).tolist(),
                         [True, False, True, True, False, False])

    def test_without_turned_edges_nothing_changes(self):
        state = np.array([True, False])
        self.assertEqual(coverage.turned_follow(self.CLOTH, [], state).tolist(), [True, False])

    def test_the_rest_rule_s_list_with_its_turned_edges_following_stays_as_it_is(self):
        # The list every body hides is the one the rest rule gives on the first body, whose turned edges
        # follow their cloth already: following them once more changes nothing.
        hidden = coverage.turned_follow(self.FACES, self.TURNED, np.array([False, True] + [False] * 4))
        self.assertEqual(hidden.tolist(), [False, True, False, False, True, True])
        self.assertEqual(coverage.turned_follow(self.FACES, self.TURNED, hidden).tolist(), hidden.tolist())


class RemovedUnderTests(unittest.TestCase):
    def test_the_body_faces_removed_under_a_pair_are_those_either_garment_covers(self):
        outer = np.array([True, False, False, True])
        inner = np.array([False, True, False, True])
        self.assertEqual(coverage.removed_under(outer, inner).tolist(), [True, True, False, True])

    def test_without_the_inner_garment_s_cover_the_outer_s_alone_is_removed(self):
        outer = np.array([True, False, False])
        removed = coverage.removed_under(outer)
        self.assertEqual(removed.tolist(), [True, False, False])
        # The outer garment's own cover is not changed.
        removed[1] = True
        self.assertEqual(outer.tolist(), [True, False, False])


class TriangleSampleTests(unittest.TestCase):
    TRIANGLE = np.array([(0.0, 0.0, 0.0), (0.02, 0.0, 0.0), (0.0, 0.02, 0.0)])

    def test_the_places_include_the_corners_the_edges_and_the_inside(self):
        weights = coverage.triangle_samples(self.TRIANGLE)
        np.testing.assert_allclose(weights.sum(axis=1), 1.0)
        rows = {tuple(np.round(w, 6)) for w in weights}
        for corner in ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)):
            self.assertIn(corner, rows)
        self.assertTrue(any(min(w) == 0.0 and max(w) < 1.0 for w in weights))
        self.assertTrue(any(min(w) > 0.0 for w in weights))
        # Neighbouring places along the longest edge lie no farther apart than the spacing.
        points = weights @ self.TRIANGLE
        self.assertLessEqual(np.linalg.norm(points[1] - points[0]), coverage.TRIANGLE_SPACING)

    def test_a_ridge_of_the_outer_surface_over_a_triangle_is_found_where_it_crosses_the_edges(self):
        # An outer triangle edge from (0.005, -0.01) to (0.005, 0.03), across the inner triangle, and an
        # outer vertex over it and one beside it.
        points = np.array([(0.004, 0.004, 0.003), (0.05, 0.05, 0.003)])
        segments = np.array([[(0.005, -0.01, 0.003), (0.005, 0.03, 0.003)]])
        weights = coverage.overlay_weights(self.TRIANGLE, points, segments)
        places = {tuple(np.round(w @ self.TRIANGLE, 6)) for w in weights}
        self.assertIn((0.004, 0.004, 0.0), places)
        self.assertIn((0.005, 0.0, 0.0), places)
        self.assertIn((0.005, 0.015, 0.0), places)
        self.assertEqual(len(places), 3)

    def test_a_degenerate_triangle_has_no_places(self):
        self.assertEqual(coverage.overlay_weights(np.zeros((3, 3)), np.ones((1, 3)), np.ones((1, 2, 3))).shape, (0, 3))

    def test_the_drawn_faces_of_the_body_split_as_the_game_splits_them(self):
        # Unity's import splits every exported quad from its first corner to its third, and a larger
        # face as a fan from its first corner; a covered face is not drawn.
        polygons = [types.SimpleNamespace(index=0, vertices=(4, 5, 6, 7)), types.SimpleNamespace(index=1, vertices=(0, 1, 2)),
                    types.SimpleNamespace(index=2, vertices=(1, 2, 3, 8, 9))]
        body = types.SimpleNamespace(data=types.SimpleNamespace(polygons=polygons))
        self.assertEqual(list(coverage.drawn_triangles(body, np.array([False, True, False]))),
                         [(0, (4, 5, 6)), (0, (4, 6, 7)), (2, (1, 2, 3)), (2, (1, 3, 8)), (2, (1, 8, 9))])


class TurnedCornerTests(unittest.TestCase):
    FACES = [(0, 1, 2, 3), (1, 4, 5, 2)]
    UVS = [[(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.0, 0.5)], [(0.5, 0.0), (1.0, 0.0), (1.0, 0.5), (0.5, 0.5)]]

    def test_faces_whose_corners_only_turned_keep_their_uvs(self):
        turned = [(1, 2, 3, 0), (1, 4, 5, 2)]
        uvs = [[(0.5, 0.0), (0.5, 0.5), (0.0, 0.5), (0.0, 0.0)], self.UVS[1]]
        self.assertTrue(texture.same_corners(self.FACES, self.UVS, turned, uvs))
        self.assertTrue(texture.same_corners(self.FACES, self.UVS, self.FACES, self.UVS))

    def test_another_winding_other_faces_or_other_uvs_are_not_the_same(self):
        self.assertFalse(texture.same_corners(self.FACES, self.UVS, [(3, 2, 1, 0), (1, 4, 5, 2)], self.UVS))
        self.assertFalse(texture.same_corners(self.FACES, self.UVS, [(0, 1, 2, 6), (1, 4, 5, 2)], self.UVS))
        self.assertFalse(texture.same_corners(self.FACES, self.UVS, self.FACES[:1], self.UVS[:1]))
        moved = [[(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.0, 0.6)], self.UVS[1]]
        self.assertFalse(texture.same_corners(self.FACES, self.UVS, self.FACES, moved))

    def test_uvs_cut_by_an_edge_loop_that_moved_no_vertex_refine_the_old_ones(self):
        # Both quads cut across at a quarter of their height: the new corners lie on the old edges.
        split = [[(0.0, 0.0), (0.5, 0.0), (0.5, 0.125), (0.0, 0.125)], [(0.0, 0.125), (0.5, 0.125), (0.5, 0.5), (0.0, 0.5)],
                 [(0.5, 0.0), (1.0, 0.0), (1.0, 0.125), (0.5, 0.125)], [(0.5, 0.125), (1.0, 0.125), (1.0, 0.5), (0.5, 0.5)]]
        self.assertTrue(texture.refines(self.UVS, split))
        self.assertTrue(texture.refines(self.UVS, self.UVS))

    def test_uvs_that_lose_a_corner_move_one_or_add_one_off_the_old_edges_do_not(self):
        self.assertFalse(texture.refines(self.UVS, self.UVS[:1]))
        moved = [[(0.0, 0.0), (0.5, 0.0), (0.5, 0.5), (0.0, 0.51)], self.UVS[1]]
        self.assertFalse(texture.refines(self.UVS, moved))
        inside = self.UVS + [[(0.2, 0.2), (0.3, 0.2), (0.3, 0.3)]]
        self.assertFalse(texture.refines(self.UVS, inside))
        self.assertFalse(texture.refines([], self.UVS))

    def test_faces_on_the_same_points_that_reach_across_two_old_faces_do_not_refine_them(self):
        # Every new corner is an old corner or lies on an old edge, and the new faces cover as much,
        # but the middle one reaches across both old faces, so the image would not lie where it lay.
        across = [[(0.0, 0.0), (0.25, 0.0), (0.25, 0.5), (0.0, 0.5)], [(0.25, 0.0), (0.75, 0.0), (0.75, 0.5), (0.25, 0.5)],
                  [(0.75, 0.0), (1.0, 0.0), (1.0, 0.5), (0.75, 0.5)]]
        self.assertFalse(texture.refines(self.UVS, across))
        within = [[(0.0, 0.0), (0.25, 0.0), (0.25, 0.5), (0.0, 0.5)], [(0.25, 0.0), (0.5, 0.0), (0.5, 0.5), (0.25, 0.5)],
                  [(0.5, 0.0), (0.75, 0.0), (0.75, 0.5), (0.5, 0.5)], [(0.75, 0.0), (1.0, 0.0), (1.0, 0.5), (0.75, 0.5)]]
        self.assertTrue(texture.refines(self.UVS, within))

    def test_faces_numbered_anew_keep_their_image_only_where_each_lies_where_its_uvs_lay(self):
        # Two quads 0.3 m apart, as two gloves, each with its own UV island.
        co = np.array([(0, 0, 0), (0.1, 0, 0), (0.1, 0, 0.1), (0, 0, 0.1),
                       (0.3, 0, 0), (0.4, 0, 0), (0.4, 0, 0.1), (0.3, 0, 0.1)], float)
        faces = [(0, 1, 2, 3), (4, 5, 6, 7)]
        # The same mesh with its vertices numbered anew, new vertex k being old vertex order[k], each
        # face with its UVs: it keeps its image.
        order = [4, 5, 6, 7, 0, 1, 2, 3]
        renumbered = [tuple(order.index(v) for v in face) for face in faces]
        self.assertTrue(texture.places_match(self.UVS, co, faces, self.UVS, co[order], renumbered))
        # The islands traded between the two quads: the same UV faces, but each on the other glove.
        self.assertTrue(texture.refines(self.UVS, self.UVS[::-1]))
        self.assertFalse(texture.places_match(self.UVS, co, faces, self.UVS[::-1], co, faces))
        # A small move of the cloth, as a rebuild's, is within the reach.
        self.assertTrue(texture.places_match(self.UVS, co, faces, self.UVS, co + np.array([0.0, 0.01, 0.0]), faces))

    def test_a_point_lies_in_a_face_inside_it_or_on_its_outline(self):
        face = np.array([(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)])
        self.assertEqual(texture.in_uv_face(np.array([(0.5, 0.5), (1.0, 0.5), (1.0 + 1e-6, 0.2), (1.1, 0.5), (0.5, -0.01)]),
                                            face).tolist(), [True, True, True, False, False])


if __name__ == "__main__":
    unittest.main()
