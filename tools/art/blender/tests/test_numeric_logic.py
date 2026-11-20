"""The numeric logic of the art pipeline that needs numpy but not Blender: the cage's net and polygons, the
weights of garments and bodies, the dyes, the paint crop, and the coverage report, on small arrays with
answers that follow from the input. CI installs numpy from requirements.txt, in the version Blender
bundles. Without numpy this file is skipped; install it, or run the tests in Blender's own Python:

    python -m pip install -r tools/art/blender/tests/requirements.txt
    "<Blender folder>/5.2/python/bin/python.exe" -m unittest discover -s tools/art/blender/tests

See blender_source.py."""

import pathlib
import sys
import unittest
import zlib

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import load  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

cage = load("art_pipeline/cage.py", "Net", "Part", "hull", "centroid", "_shares", "_along", "_turning", "REGIONS",
            "middle_sides", "keep_aside", "away_from_middle", "mirror_images", "twins_of", "evened", "drawn", "MIDDLE",
            "nearest_on_segments", numpy=True)
garment = load("art_pipeline/garment.py", "_limited", "_checksum", "_rounded_weights", "MAX_INFLUENCES", "version_name",
               numpy=True)
bodies = load("art_pipeline/bodies.py", "place_keys", "mirror_key", "ZONES")
rig = load("human_base/rig.py", "_mirror_columns", "_mirrored", "symmetrize", "limit", "_move_excess", "_segment_distance",
           "_smooth", "_around", "hip_boundary", "evened_out", "crotch_line", "line_distance", "HIP_BOUNDARY",
           "HIP_BOUNDARY_REBUILT", "MIN_WEIGHT", "MAX_INFLUENCES", bodies=bodies, numpy=True)
paint = load("art_pipeline/paint.py", "crop", "_turn", "_landmark_pairs", "CROP_BLOCK", "MIRROR_MARGIN", "LANDMARK_MERGE",
             numpy=True)
texture = load("art_pipeline/texture.py", "srgb_to_linear", "linear_to_srgb", "color_linear", "_hex", "dye_factor", "open_loops",
               "hang_lines", "nearest_on", "spread", "drawn", "sampled", "grown", "CURVE_REACH", "SIDE_REACH", "BAKE_MARGIN",
               numpy=True)
coverage = load("art_pipeline/coverage.py", "Coverage", "summary", garment=garment, numpy=True)

# Three rows of three vertices, in four quads, counterclockwise seen from +Z:
#   6 7 8
#   3 4 5
#   0 1 2
GRID = [(0, 1, 4, 3), (1, 2, 5, 4), (3, 4, 7, 6), (4, 5, 8, 7)]
GRID_CO = np.array([(col, row, 0.0) for row in range(3) for col in range(3)])


class NetTests(unittest.TestCase):
    def setUp(self):
        self.net = cage.Net(GRID, 9)

    def test_the_border_is_every_vertex_but_the_middle_one(self):
        self.assertEqual(self.net.on_border.tolist(), [True] * 4 + [False] + [True] * 4)
        self.assertEqual(len(self.net.border_edges), 8)
        self.assertEqual(len(self.net.edges), 12)

    def test_a_border_vertex_counts_only_its_neighbors_along_the_border(self):
        self.assertEqual(self.net.degree.tolist(), [2, 2, 2, 2, 4, 2, 2, 2, 2])

    def test_the_faces_are_cut_into_triangles_fanned_from_their_first_corner(self):
        self.assertEqual(self.net.triangles.tolist()[:2], [[0, 1, 4], [0, 4, 3]])
        self.assertEqual(len(self.net.triangles), 8)

    def test_the_open_end_is_one_loop_of_its_vertices_in_the_order_they_follow_each_other(self):
        (loop,) = self.net.loops()
        self.assertEqual(sorted(loop), [0, 1, 2, 3, 5, 6, 7, 8])
        border = {tuple(sorted(map(int, e))) for e in self.net.border_edges}
        for a, b in zip(loop, loop[1:] + loop[:1]):
            self.assertIn(tuple(sorted((a, b))), border)

    def test_a_closed_net_has_no_open_end(self):
        tetrahedron = cage.Net([(0, 1, 2), (0, 3, 1), (1, 3, 2), (2, 3, 0)], 4)
        self.assertEqual(tetrahedron.loops(), [])
        self.assertFalse(tetrahedron.on_border.any())
        self.assertEqual(tetrahedron.degree.tolist(), [3, 3, 3, 3])

    def test_two_open_ends_are_two_loops(self):
        # A tube of two rings of three vertices, closed by quads around: both rings are open ends.
        tube = cage.Net([(0, 1, 4, 3), (1, 2, 5, 4), (2, 0, 3, 5)], 6)
        loops = tube.loops()
        self.assertEqual(sorted(sorted(loop) for loop in loops), [[0, 1, 2], [3, 4, 5]])

    def test_the_hops_from_a_vertex_count_edges_and_stop_at_the_limit(self):
        self.assertEqual(self.net.hops_from(np.array([0]), 4).tolist(), [0, 1, 2, 1, 2, 3, 2, 3, 4])
        self.assertEqual(self.net.hops_from(np.array([0]), 2).tolist(), [0, 1, 2, 1, 2, 2, 2, 2, 2])
        self.assertEqual(self.net.hops_from(np.array([0, 8]), 4).tolist(), [0, 1, 2, 1, 2, 1, 2, 1, 0])

    def test_the_mean_of_the_neighbors_keeps_the_middle_and_pulls_a_corner_along_the_border(self):
        mean = self.net.mean_of_neighbors(GRID_CO)
        self.assertEqual(mean[4].tolist(), [1.0, 1.0, 0.0])
        self.assertEqual(mean[0].tolist(), [0.5, 0.5, 0.0])
        self.assertEqual(mean[1].tolist(), [1.0, 0.0, 0.0])

    def test_a_flat_net_has_normals_straight_up_and_smoothing_does_not_tilt_them(self):
        np.testing.assert_allclose(self.net.normals(GRID_CO), np.tile([0.0, 0.0, 1.0], (9, 1)), atol=1e-12)
        np.testing.assert_allclose(self.net.smoothed_normals(GRID_CO, 5), np.tile([0.0, 0.0, 1.0], (9, 1)), atol=1e-12)

    def test_fairing_keeps_a_held_vertex_where_it_is_and_a_flat_net_flat(self):
        raised = GRID_CO.copy()
        raised[4, 2] = 0.5
        faired = self.net.faired(raised, 4, held=np.array([4]))
        self.assertEqual(faired[4].tolist(), [1.0, 1.0, 0.5])
        flat = self.net.faired(GRID_CO, 4)
        np.testing.assert_allclose(flat[:, 2], 0.0, atol=1e-12)


class PartTests(unittest.TestCase):
    CO = np.array([(col - 1.0, row, 0.0) for row in range(3) for col in range(3)])

    def test_a_part_numbers_the_vertices_of_the_faces_it_keeps_among_themselves(self):
        part = cage.Part(GRID, np.array([1, 1, 2, 2]), ("arms",))
        self.assertEqual(part.vertices.tolist(), [3, 4, 5, 6, 7, 8])
        self.assertEqual(part.faces, [(0, 1, 4, 3), (1, 2, 5, 4)])
        self.assertEqual(part.regions.tolist(), [2, 2])
        self.assertEqual(part.net.count, 6)

    def test_a_vertex_between_two_regions_belongs_to_the_later_and_a_limb_has_a_side(self):
        part = cage.Part(GRID, np.array([1, 1, 2, 2]), ("trunk", "arms"))
        keys = part.keys(self.CO)
        self.assertEqual(keys[:3], [("trunk", "")] * 3)
        self.assertEqual(keys[3:], [("arms", "L"), ("arms", "R"), ("arms", "R")] * 2)

    def test_the_borders_are_the_open_ends_and_the_vertices_between_regions(self):
        one = cage.Part(GRID, np.array([1, 1, 1, 1]), ("trunk",))
        self.assertEqual(one.borders().tolist(), [0, 1, 2, 3, 5, 6, 7, 8])
        two = cage.Part(GRID, np.array([1, 1, 1, 2]), ("trunk", "arms"))
        self.assertEqual(two.borders().tolist(), list(range(9)))

    def test_a_limb_meets_the_trunk_at_the_vertices_they_share_and_the_neck_does_not(self):
        arm = cage.Part(GRID, np.array([1, 1, 2, 2]), ("trunk", "arms"))
        self.assertEqual(np.nonzero(arm.limb_roots())[0].tolist(), [3, 4, 5])
        neck = cage.Part(GRID, np.array([0, 0, 1, 1]), ("neck", "trunk"))
        self.assertFalse(neck.limb_roots().any())
        # A hand hangs from an arm, not from the trunk.
        hand = cage.Part(GRID, np.array([2, 2, 4, 4]), ("arms", "hands"))
        self.assertFalse(hand.limb_roots().any())


class FoldTests(unittest.TestCase):
    # Two edges of a fold that meet at a right angle at (1, 0, 0).
    FOLD = np.array([[(0.0, 0.0, 0.0), (1.0, 0.0, 0.0)], [(1.0, 0.0, 0.0), (1.0, 2.0, 0.0)]])

    def test_the_nearest_point_of_a_fold_lies_on_the_edge_beside_the_point(self):
        np.testing.assert_allclose(cage.nearest_on_segments(np.array((0.25, 0.5, 3.0)), self.FOLD), (0.25, 0.0, 0.0))
        np.testing.assert_allclose(cage.nearest_on_segments(np.array((0.9, 1.5, -1.0)), self.FOLD), (1.0, 1.5, 0.0))

    def test_past_the_end_of_an_edge_the_end_is_nearest(self):
        np.testing.assert_allclose(cage.nearest_on_segments(np.array((-2.0, -1.0, 0.0)), self.FOLD), (0.0, 0.0, 0.0))
        np.testing.assert_allclose(cage.nearest_on_segments(np.array((3.0, 5.0, 0.0)), self.FOLD), (1.0, 2.0, 0.0))

    def test_an_edge_without_length_is_its_point(self):
        point = np.array([[(2.0, 2.0, 2.0), (2.0, 2.0, 2.0)]])
        np.testing.assert_allclose(cage.nearest_on_segments(np.array((0.0, 0.0, 0.0)), point), (2.0, 2.0, 2.0))


class MiddlePlaneTests(unittest.TestCase):
    # A vertex of the trunk on the body's right, one on its left, one in the middle plane, and one
    # of a leg, which has a side of its own.
    KEYS = [("trunk", ""), ("trunk", ""), ("neck", ""), ("legs", "R")]
    REFERENCE = np.array([(0.02, 0.0, 1.0), (-0.02, 0.0, 1.0), (0.0, 0.1, 1.5), (0.1, 0.0, 0.5)])

    def setUp(self):
        self.aside = cage.middle_sides(self.KEYS, self.REFERENCE)

    def test_a_vertex_of_the_trunk_has_a_side_of_the_middle_plane_and_one_of_a_limb_none(self):
        self.assertEqual(self.aside[:3].tolist(), [1.0, -1.0, 0.0])
        self.assertTrue(np.isnan(self.aside[3]))
        # A vertex closer to the plane than MIDDLE lies in it.
        near = cage.middle_sides([("trunk", "")] * 2, np.array([(cage.MIDDLE / 2, 0, 0), (cage.MIDDLE * 2, 0, 0)]))
        self.assertEqual(near.tolist(), [0.0, 1.0])

    def test_a_vertex_that_crossed_the_plane_or_left_it_goes_back_into_it(self):
        moved = np.array([(-0.004, 0.1, 1.0), (0.003, 0.2, 1.0), (0.005, 0.3, 1.5), (-0.2, 0.4, 0.5)])
        kept = cage.keep_aside(moved, self.aside)
        self.assertEqual(kept[:, 0].tolist(), [0.0, 0.0, 0.0, -0.2])
        np.testing.assert_array_equal(kept[:, 1:], moved[:, 1:])
        np.testing.assert_array_equal(moved[:, 0], [-0.004, 0.003, 0.005, -0.2])

    def test_a_vertex_on_its_own_side_stays_where_it_is(self):
        there = np.array([(0.03, 0.0, 1.0), (-0.01, 0.0, 1.0), (0.0, 0.0, 1.5), (0.1, 0.0, 0.5)])
        np.testing.assert_array_equal(cage.keep_aside(there, self.aside), there)

    def test_a_direction_loses_its_part_toward_the_plane_and_keeps_the_length_that_is_left(self):
        directions = np.array([(-0.6, 0.0, -0.8), (-0.6, 0.0, -0.8), (0.6, 0.8, 0.0), (-1.0, 0.0, 0.0)])
        unit, left = cage.away_from_middle(directions, self.aside)
        # The right vertex was headed for the plane: straight down is left of its direction.
        np.testing.assert_allclose(unit[0], (0.0, 0.0, -1.0))
        self.assertAlmostEqual(left[0], 0.8)
        # The left vertex was headed away from it and keeps its direction.
        np.testing.assert_allclose(unit[1], (-0.6, 0.0, -0.8))
        self.assertAlmostEqual(left[1], 1.0)
        # A vertex in the plane moves in the plane only.
        np.testing.assert_allclose(unit[2], (0.0, 1.0, 0.0))
        self.assertAlmostEqual(left[2], 0.8)
        # A limb has no such rule.
        np.testing.assert_allclose(unit[3], (-1.0, 0.0, 0.0))

    def test_a_direction_straight_at_the_plane_leaves_nothing(self):
        unit, left = cage.away_from_middle(np.array([(-1.0, 0.0, 0.0)]), np.array([1.0]))
        self.assertEqual(left[0], 0.0)
        self.assertTrue(np.isfinite(unit).all())


class MirrorTests(unittest.TestCase):
    POINTS = np.array([(0.3, 1.0, 2.0), (0.0, 0.5, 0.5), (-0.3, 1.0, 2.0), (0.1, 0.0, 0.0), (-0.1, 0.0, 0.0)])

    def test_every_point_finds_its_mirror_image_and_one_in_the_plane_itself(self):
        nearest, distances = cage.mirror_images(self.POINTS)
        self.assertEqual(nearest.tolist(), [2, 1, 0, 4, 3])
        np.testing.assert_allclose(distances, 0.0, atol=1e-12)
        self.assertEqual(cage.twins_of(self.POINTS, 1e-6).tolist(), [2, 1, 0, 4, 3])

    def test_the_distance_to_an_image_is_measured_exactly(self):
        points = self.POINTS.copy()
        points[2] += (0.0, 3e-6, 4e-6)
        _nearest, distances = cage.mirror_images(points)
        np.testing.assert_allclose(distances[[0, 2]], 5e-6, rtol=1e-6)
        self.assertIsNone(cage.twins_of(points, 1e-6))
        self.assertEqual(cage.twins_of(points, 1e-5).tolist(), [2, 1, 0, 4, 3])

    def test_points_without_an_image_or_that_share_one_have_no_twins(self):
        self.assertIsNone(cage.twins_of(self.POINTS[:4], 1e-3))
        # Two points at one place, as the lips of a closed mouth, have images but are not twins of one.
        shared = np.concatenate([self.POINTS, self.POINTS[:1]])
        self.assertLess(cage.mirror_images(shared)[1].max(), 1e-12)
        self.assertIsNone(cage.twins_of(shared, 1e-3))

    def test_many_points_are_matched_block_by_block(self):
        rng = np.random.default_rng(7)
        half = rng.uniform(0.1, 1.0, (700, 3))
        points = np.concatenate([half, half * (-1.0, 1.0, 1.0)])
        twins = cage.twins_of(points, 1e-9)
        self.assertEqual(twins.tolist(), list(range(700, 1400)) + list(range(700)))

    def test_evening_out_lays_twins_alike_and_a_vertex_in_the_plane_into_it(self):
        twins = cage.twins_of(self.POINTS, 1e-6)
        fitted = self.POINTS + np.array([(0.02, 0.0, 0.0), (0.004, 0.01, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 0.06)])
        even = cage.evened(fitted, twins)
        np.testing.assert_allclose(even[0], (0.31, 1.0, 2.0))
        np.testing.assert_allclose(even[2], (-0.31, 1.0, 2.0))
        np.testing.assert_allclose(even[1], (0.0, 0.51, 0.5))
        np.testing.assert_allclose(even[[3, 4], 2], 0.03)
        np.testing.assert_allclose(even[twins] * (-1.0, 1.0, 1.0), even)

    def test_evening_out_moves_only_the_moving_vertices_and_nothing_without_twins(self):
        twins = cage.twins_of(self.POINTS, 1e-6)
        fitted = self.POINTS + np.array([(0.02, 0.0, 0.0)] + [(0.0, 0.0, 0.0)] * 4)
        even = cage.evened(fitted, twins, np.array([True, True, False, True, True]))
        np.testing.assert_allclose(even[0], (0.31, 1.0, 2.0))
        np.testing.assert_array_equal(even[2], fitted[2])
        self.assertIs(cage.evened(fitted, None), fitted)


class DrawTests(unittest.TestCase):
    def setUp(self):
        self.net = cage.Net(GRID, 9)
        self.start = GRID_CO + (0.0, 0.0, 0.5)
        self.all = np.ones(9, bool)

    @staticmethod
    def floor(x):
        """The skin is the floor: every vertex is drawn straight down to it."""
        return x * (1.0, 1.0, 0.0)

    def test_the_last_round_lays_every_moving_vertex_on_the_skin(self):
        drawn = cage.drawn(self.net, self.start, self.floor, self.all)
        np.testing.assert_allclose(drawn, GRID_CO, atol=1e-12)
        np.testing.assert_array_equal(self.start[:, 2], 0.5)

    def test_a_held_vertex_stays_and_the_others_still_reach_the_skin(self):
        moving = self.all.copy()
        moving[4] = False
        drawn = cage.drawn(self.net, self.start, self.floor, moving)
        np.testing.assert_array_equal(drawn[4], self.start[4])
        np.testing.assert_allclose(drawn[moving, 2], 0.0, atol=1e-12)

    def test_without_evening_a_vertex_goes_to_the_skin_by_itself(self):
        def aside(x):
            target = x.copy()
            target[4] = (5.0, 5.0, 0.0)
            return target

        np.testing.assert_array_equal(cage.drawn(self.net, self.start, aside, self.all, rounds=(0,)), aside(self.start))

    def test_evening_carries_a_vertex_along_with_its_neighbors(self):
        def all_but_the_middle(x):
            target = x + (1.0, 0.0, 0.0)
            target[4] = x[4]
            return target

        drawn = cage.drawn(self.net, self.start, all_but_the_middle, self.all, rounds=(4,))
        # The middle vertex asked for no move of its own and takes most of its neighbors'.
        self.assertGreater(drawn[4, 0] - self.start[4, 0], 0.5)
        self.assertLess(drawn[4, 0] - self.start[4, 0], 1.0)
        # The vertices of the open end even out along the end only, so they keep theirs.
        np.testing.assert_allclose(np.delete(drawn[:, 0] - self.start[:, 0], 4), 1.0)

    def test_a_round_evened_out_more_moves_the_net_more_alike(self):
        def one_corner(x):
            target = x.copy()
            target[0, 0] += 1.0
            return target

        spreads = [np.ptp(cage.drawn(self.net, self.start, one_corner, self.all, rounds=(times,))[:, 0] - self.start[:, 0])
                   for times in (0, 1, 6)]
        self.assertGreater(spreads[0], spreads[1])
        self.assertGreater(spreads[1], spreads[2])


class PolygonTests(unittest.TestCase):
    SQUARE = np.array([(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)])

    def test_the_hull_of_points_is_their_outline_counterclockwise_without_interior_or_collinear_points(self):
        points = np.array([(0, 0), (2, 0), (2, 2), (0, 2), (1, 1), (1, 0), (0.5, 0.5)], float)
        self.assertEqual(cage.hull(points).tolist(), [[0, 0], [2, 0], [2, 2], [0, 2]])

    def test_the_hull_ignores_the_third_coordinate_and_repeats(self):
        points = np.array([(0, 0, 7), (0, 0, 8), (1, 0, 0), (0, 1, 0)], float)
        self.assertEqual(cage.hull(points).tolist(), [[0, 0], [1, 0], [0, 1]])

    def test_fewer_than_three_points_are_their_own_hull(self):
        self.assertEqual(cage.hull(np.array([(1.0, 1.0), (0.0, 0.0), (1.0, 1.0)])).tolist(), [[0.0, 0.0], [1.0, 1.0]])
        self.assertEqual(cage.hull(np.zeros((0, 2))).shape, (0, 2))

    def test_the_centre_of_an_area_is_where_its_weight_balances(self):
        np.testing.assert_allclose(cage.centroid(self.SQUARE * 2), [1.0, 1.0])
        np.testing.assert_allclose(cage.centroid(np.array([(0.0, 0.0), (3.0, 0.0), (0.0, 3.0)])), [1.0, 1.0])
        np.testing.assert_allclose(cage.centroid(np.array([(0, 0), (4, 0), (4, 2), (0, 2)], float)[::-1]), [2.0, 1.0])

    def test_the_corners_lie_at_shares_of_the_outline(self):
        np.testing.assert_allclose(cage._shares(self.SQUARE), [0.0, 0.25, 0.5, 0.75])
        # A triangle with sides 3, 1, and the square root of 10.
        outline = 4 + np.sqrt(10)
        np.testing.assert_allclose(cage._shares(np.array([(0.0, 0.0), (3.0, 0.0), (3.0, 1.0)])),
                                   [0.0, 3 / outline, 4 / outline])

    def test_points_at_shares_of_the_outline_start_at_the_first_corner_and_close_the_loop(self):
        got = cage._along(self.SQUARE, np.array([0.0, 0.125, 0.25, 0.5, 0.875, 1.0]))
        np.testing.assert_allclose(got, [[0, 0], [0.5, 0], [1, 0], [1, 1], [0, 0.5], [0, 0]])

    def test_a_polygon_turns_counterclockwise_or_clockwise(self):
        self.assertEqual(cage._turning(self.SQUARE), 1.0)
        self.assertEqual(cage._turning(self.SQUARE[::-1]), -1.0)
        self.assertEqual(cage._turning(np.array([(0.0, 0.0), (1.0, 0.0), (2.0, 0.0)])), 0.0)


class GarmentWeightTests(unittest.TestCase):
    def test_a_vertex_keeps_its_four_largest_weights_adding_up_to_one(self):
        limited = garment._limited(np.array([0.1, 0.2, 0.3, 0.4, 0.5]))
        np.testing.assert_allclose(limited, [0.0, 0.2 / 1.4, 0.3 / 1.4, 0.4 / 1.4, 0.5 / 1.4])
        self.assertAlmostEqual(limited.sum(), 1.0)

    def test_a_row_of_up_to_four_weights_that_adds_up_to_one_is_unchanged(self):
        row = np.array([0.5, 0.0, 0.25, 0.25])
        np.testing.assert_allclose(garment._limited(row), row)

    def test_a_row_without_weight_stays_zero(self):
        self.assertEqual(garment._limited(np.zeros(6)).tolist(), [0.0] * 6)

    def test_limiting_leaves_the_row_it_was_given_alone(self):
        row = np.array([0.1, 0.2, 0.3, 0.4, 0.5])
        garment._limited(row)
        self.assertEqual(row.tolist(), [0.1, 0.2, 0.3, 0.4, 0.5])

    def test_the_checksum_of_a_vertex_is_the_crc_of_its_named_weights_in_bone_order(self):
        names = ["Hips", "Spine", "Chest"]
        expected = zlib.crc32(b"Hips:0.2500;Chest:0.7500")
        expected = expected - (1 << 32) if expected >= 1 << 31 else expected
        self.assertEqual(garment._checksum(np.array([0.25, 0.0, 0.75]), names), expected)

    def test_the_checksum_is_a_signed_32_bit_integer(self):
        names = [f"Bone{i}" for i in range(4)]
        for i in range(200):
            row = np.array([i / 200, 1 - i / 200, 0.0, 0.0])
            value = garment._checksum(row, names)
            self.assertTrue(-(1 << 31) <= value < (1 << 31), value)

    def test_a_weight_is_part_of_the_checksum_to_a_ten_thousandth_and_a_zero_is_not(self):
        names = ["A", "B"]
        base = garment._checksum(np.array([0.5, 0.5]), names)
        self.assertEqual(garment._checksum(np.array([0.50001, 0.5]), names), base)
        self.assertNotEqual(garment._checksum(np.array([0.5001, 0.5]), names), base)
        self.assertEqual(garment._checksum(np.array([0.5, 0.0]), names), garment._checksum(np.array([0.5, 0.0]), ["A", "Other"]))

    def test_weights_are_rounded_the_way_vertex_groups_store_them(self):
        rounded = garment._rounded_weights(np.array([0.1, 0.5, 1.0]))
        self.assertEqual(rounded.dtype, np.float64)
        self.assertEqual(rounded.tolist(), [float(np.float32(0.1)), 0.5, 1.0])
        self.assertNotEqual(rounded[0], 0.1)

    def test_at_most_four_influences(self):
        self.assertEqual(garment.MAX_INFLUENCES, 4)


class BodyWeightTests(unittest.TestCase):
    NAMES = ["A_L", "A_R", "B_L", "B_R", "C"]

    def test_a_bone_and_its_mirror_are_found_and_a_centre_bone_is_its_own(self):
        self.assertEqual(rig._mirror_columns(self.NAMES).tolist(), [1, 0, 3, 2, 4])

    def test_a_side_bone_without_its_mirror_is_an_error(self):
        with self.assertRaises(ValueError):
            rig._mirror_columns(["A_L", "C"])

    def test_the_vertices_that_mirror_each_other_across_the_middle_are_found(self):
        points = np.array([(1.0, 2.0, 3.0), (-1.0, 2.0, 3.0), (0.0, 5.0, 5.0)])
        self.assertEqual(rig._mirrored(points, np.arange(3)).tolist(), [1, 0, 2])
        self.assertEqual(rig._mirrored(points, np.array([1])).tolist(), [0])

    def test_a_vertex_without_a_mirror_is_an_error(self):
        with self.assertRaises(KeyError):
            rig._mirrored(np.array([(1.0, 2.0, 3.0), (-1.5, 2.0, 3.0)]), np.array([0]))

    def test_vertices_at_one_place_mirror_in_their_order(self):
        # The upper lip's vertex and the lower lip's at one place on each side, the upper first.
        points = np.array([(1.0, 2.0, 3.0), (1.0, 2.0, 3.0), (-1.0, 2.0, 3.0), (-1.0, 2.0, 3.0)])
        self.assertEqual(rig._mirrored(points, np.arange(4)).tolist(), [2, 3, 0, 1])

    def test_a_place_key_counts_the_vertices_before_it_at_that_place(self):
        keys = bodies.place_keys([(0.1, 0.2, 0.3), (0.1, 0.2, 0.3), (0.4, 0.5, 0.6)])
        self.assertEqual(keys, [(10000, 20000, 30000, 0), (10000, 20000, 30000, 1), (40000, 50000, 60000, 0)])
        self.assertEqual(bodies.mirror_key(keys[1]), (-10000, 20000, 30000, 1))

    def test_the_left_takes_the_mirrored_weights_of_the_right_and_the_middle_the_mean_of_both(self):
        points = np.array([(1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 0.0, 0.0)])
        weights = np.array([[0.3, 0.7, 0.0], [0.5, 0.5, 0.0], [0.4, 0.0, 0.6]])
        result = rig.symmetrize(points, weights, ["A_L", "A_R", "C"])
        np.testing.assert_allclose(result, [[0.3, 0.7, 0.0], [0.7, 0.3, 0.0], [0.2, 0.2, 0.6]])
        # The input stays as it was.
        self.assertEqual(weights[1].tolist(), [0.5, 0.5, 0.0])

    def test_a_weight_below_the_least_is_dropped_and_the_row_adds_up_to_one(self):
        weights = np.array([[0.5, 0.49, 0.0, 0.0, 0.01]])
        result = rig.limit(weights, self.NAMES)
        self.assertEqual(result[0, 4], 0.0)
        self.assertAlmostEqual(result.sum(), 1.0)
        np.testing.assert_allclose(result[0, :2], [0.5 / 0.99, 0.49 / 0.99])

    def test_at_most_four_bones_stay_and_a_bone_and_its_mirror_stay_or_go_together(self):
        weights = np.array([[0.3, 0.3, 0.1, 0.1, 0.2]])
        result = rig.limit(weights, self.NAMES)
        np.testing.assert_allclose(result, [[0.375, 0.375, 0.125, 0.125, 0.0]])
        self.assertLessEqual(int((result > 0).sum()), rig.MAX_INFLUENCES)

    def test_a_vertex_with_few_enough_bones_is_only_normalized(self):
        result = rig.limit(np.array([[0.2, 0.0, 0.2, 0.0, 0.0]]), self.NAMES)
        np.testing.assert_allclose(result, [[0.5, 0.0, 0.5, 0.0, 0.0]])

    def test_the_excess_above_a_cap_goes_to_the_strongest_target_or_the_first_where_none_has_weight(self):
        weights = np.array([[0.9, 0.1, 0.0], [0.5, 0.0, 0.5], [0.8, 0.0, 0.0]])
        rig._move_excess(weights, np.array([0, 1, 2]), 0, 0.6, [1, 2])
        np.testing.assert_allclose(weights, [[0.6, 0.4, 0.0], [0.5, 0.0, 0.5], [0.6, 0.2, 0.0]], atol=1e-12)

    def test_the_distance_to_a_segment_is_to_its_nearest_point_not_to_its_line(self):
        a, b = np.array([0.0, 0.0, 0.0]), np.array([2.0, 0.0, 0.0])
        points = np.array([(1.0, 1.0, 0.0), (-1.0, 0.0, 0.0), (3.0, 0.0, 4.0), (1.0, 0.0, 0.0)])
        np.testing.assert_allclose(rig._segment_distance(points, a, b), [1.0, 1.0, np.sqrt(17), 0.0])
        np.testing.assert_allclose(rig._segment_distance(points, a, a), [np.sqrt(2), 1.0, 5.0, 1.0])

    def test_the_smooth_step_runs_from_zero_to_one_between_its_edges(self):
        self.assertEqual(rig._smooth(0.0, 2.0, np.array([-1.0, 0.0, 1.0, 2.0, 5.0])).tolist(), [0.0, 0.0, 0.5, 1.0, 1.0])
        self.assertEqual(rig._smooth(2.0, 0.0, np.array([0.0, 2.0])).tolist(), [1.0, 0.0])

    def test_two_unit_vectors_across_an_axis_are_as_forward_and_outward_as_they_can_be(self):
        axis = np.array([0.0, 0.0, 1.0])
        forward, outward = rig._around(axis, np.array([0.0, 1.0, 1.0]), np.array([1.0, 1.0, 0.0]))
        np.testing.assert_allclose(forward, [0.0, 1.0, 0.0])
        np.testing.assert_allclose(outward, [1.0, 0.0, 0.0], atol=1e-12)
        self.assertAlmostEqual(forward @ outward, 0.0)
        self.assertAlmostEqual(forward @ axis, 0.0)

    def test_a_hip_boundary_runs_straight_between_its_rows_and_on_from_the_last_to_the_first(self):
        boundary = ((0, 0.0, 0.1), (90, 0.2, 0.3), (180, 0.0, 0.1))
        below, width = rig.hip_boundary(boundary, [0.0, 45.0, 90.0, 135.0, 270.0, 405.0, -90.0])
        np.testing.assert_allclose(below, [0.0, 0.1, 0.2, 0.1, 0.0, 0.1, 0.0], atol=1e-12)
        np.testing.assert_allclose(width, [0.1, 0.2, 0.3, 0.2, 0.1, 0.2, 0.1], atol=1e-12)

    def test_the_rebuilt_bodies_groin_rises_from_the_crotch_to_under_the_hip_bone_and_comes_down_to_the_side(self):
        # Negative is above the hip joint. From the inner side over the front to 20 degrees outside
        # it, the boundary rises all the way, so the legs meet the trunk in a V.
        rising, _widths = rig.hip_boundary(rig.HIP_BOUNDARY_REBUILT, [270.0, 300.0, 325.0, 345.0, 0.0, 10.0, 20.0])
        self.assertTrue((np.diff(rising) < 0.0).all())
        # From there it comes down to the outer side, and nowhere does it lie higher.
        falling, _widths = rig.hip_boundary(rig.HIP_BOUNDARY_REBUILT, [20.0, 35.0, 50.0, 70.0, 90.0])
        self.assertTrue((np.diff(falling) > 0.0).all())
        everywhere, widths = rig.hip_boundary(rig.HIP_BOUNDARY_REBUILT, np.arange(0.0, 360.0, 1.0))
        self.assertEqual(int(np.argmin(everywhere)), 20)
        self.assertTrue((widths > 0.0).all())

    def test_the_current_bodies_groin_lies_level_from_the_front_to_the_outer_side(self):
        level, _widths = rig.hip_boundary(rig.HIP_BOUNDARY, [0.0, 30.0, 60.0, 90.0])
        self.assertEqual(len(set(level.tolist())), 1)

    def test_evening_out_moves_the_given_rows_halfway_to_their_neighbours_each_time(self):
        # The middle vertex of the grid holds all of one bone, its neighbours along the edges none.
        weights = np.zeros((9, 2))
        weights[:, 1] = 1.0
        weights[4] = (1.0, 0.0)
        once = rig.evened_out(weights.copy(), np.array([4]), GRID, 1)
        np.testing.assert_allclose(once[4], [0.5, 0.5])
        twice = rig.evened_out(weights.copy(), np.array([4]), GRID, 2)
        np.testing.assert_allclose(twice[4], [0.25, 0.75])
        # The other rows keep their weights, and every row still adds up to one.
        np.testing.assert_allclose(np.delete(twice, 4, axis=0), np.delete(weights, 4, axis=0))
        np.testing.assert_allclose(twice.sum(axis=1), 1.0)

    def test_the_distance_to_a_line_is_to_its_nearest_stretch_and_a_line_of_one_place_is_a_point(self):
        line = np.array([(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0)])
        points = np.array([(0.5, 1.0, 0.0), (-1.0, 0.0, 0.0), (1.0, 0.5, 2.0), (0.25, 0.0, 0.0)])
        np.testing.assert_allclose(rig.line_distance(points, line), [0.5, 1.0, 2.0, 0.0])
        np.testing.assert_allclose(rig.line_distance(points, line[:1]), [np.sqrt(1.25), 1.0, np.sqrt(5.25), 0.25])

    def test_the_crotchs_middle_line_runs_under_the_trunk_from_the_back_and_up_its_front_to_the_hip_joint(self):
        # A body's middle line in no order: the cleft at the back, the underside, the front up to
        # the belly. The hip joint lies at 0.90, and the underside is what lies within 3 cm of
        # the lowest place.
        under = [(0.0, -0.04, 0.83), (0.0, 0.0, 0.82), (0.0, 0.05, 0.81), (0.0, 0.09, 0.82)]
        front = [(0.0, 0.11, 0.85), (0.0, 0.13, 0.88)]
        cleft, belly = (0.0, -0.08, 0.90), (0.0, 0.14, 0.93)
        centre = np.array([front[1], under[2], cleft, under[0], belly, front[0], under[3], under[1]])
        line = rig.crotch_line(centre, np.array([0.09, 0.015, 0.90]), 0.03)
        np.testing.assert_allclose(line, under + front)

    def test_evening_out_takes_every_row_from_the_weights_before_the_step(self):
        # Rows 3, 4, 5 across the middle of the grid: 4 has 3 and 5 among its neighbours along the
        # edges, and each of them has 4. A step moves all three from the weights they had.
        weights = np.zeros((9, 1))
        weights[4, 0] = 1.0
        evened = rig.evened_out(weights, np.array([3, 4, 5]), GRID, 1)
        np.testing.assert_allclose(evened[[3, 4, 5], 0], [1.0 / 6.0, 0.5, 1.0 / 6.0])


class PaintTests(unittest.TestCase):
    def test_a_crop_starts_at_whole_blocks_with_a_block_of_empty_texels_around_the_paint(self):
        image = np.zeros((32, 32, 4))
        image[10, 12, 3] = 1.0
        cropped, rect = paint.crop(image)
        self.assertEqual(rect, [8, 4, 12, 12])
        self.assertEqual(cropped.shape, (12, 12, 4))
        self.assertEqual(cropped[10 - 4, 12 - 8, 3], 1.0)
        self.assertEqual(cropped.sum(), 1.0)

    def test_a_crop_never_leaves_the_image(self):
        image = np.zeros((20, 20, 4))
        image[0, 0, 3] = 1.0
        image[19, 19, 3] = 0.5
        cropped, rect = paint.crop(image)
        self.assertEqual(rect, [0, 0, 20, 20])
        self.assertEqual(cropped.shape, (20, 20, 4))

    def test_the_crop_of_a_corner_pixel_is_two_blocks_square(self):
        image = np.zeros((64, 64, 4))
        image[0, 0, 3] = 1.0
        self.assertEqual(paint.crop(image)[1], [0, 0, 8, 8])

    def test_an_unpainted_image_crops_to_a_block_at_the_corner(self):
        cropped, rect = paint.crop(np.zeros((16, 16, 4)))
        self.assertEqual(rect, [0, 0, paint.CROP_BLOCK, paint.CROP_BLOCK])
        self.assertEqual(cropped.shape, (paint.CROP_BLOCK, paint.CROP_BLOCK, 4))

    def test_the_crop_is_a_copy_not_a_view_of_the_image(self):
        image = np.zeros((16, 16, 4))
        image[5, 5, 3] = 1.0
        cropped, _rect = paint.crop(image)
        cropped[:] = 7.0
        self.assertEqual(image.max(), 1.0)

    def test_a_vector_is_turned_by_the_rotation_that_takes_one_direction_to_another(self):
        x, y, z = np.eye(3)
        np.testing.assert_allclose(paint._turn(x.copy(), x, y), y, atol=1e-12)
        np.testing.assert_allclose(paint._turn(y.copy(), x, y), -x, atol=1e-12)
        np.testing.assert_allclose(paint._turn(z.copy(), x, y), z, atol=1e-12)

    def test_a_vector_is_not_turned_when_the_directions_are_the_same(self):
        x = np.array([1.0, 0.0, 0.0])
        np.testing.assert_allclose(paint._turn(np.array([0.3, 0.4, 0.5]), x, x), [0.3, 0.4, 0.5])

    def test_a_landmark_the_rigs_keep_for_one_side_only_is_mirrored_for_the_other(self):
        canvas = {"a": [1.0, 2.0, 3.0]}
        body = {"a": [4.0, 5.0, 6.0]}
        source, target = paint._landmark_pairs(canvas, body)
        self.assertEqual(source.tolist(), [[1.0, 2.0, 3.0], [-1.0, 2.0, 3.0]])
        self.assertEqual(target.tolist(), [[4.0, 5.0, 6.0], [-4.0, 5.0, 6.0]])

    def test_a_landmark_with_both_sides_or_on_the_centre_line_is_not_mirrored_again(self):
        both = paint._landmark_pairs({"b": [1.0, 2.0, 3.0, -1.0, 2.0, 3.0]}, {"b": [4.0, 5.0, 6.0, -4.0, 5.0, 6.0]})
        self.assertEqual(len(both[0]), 2)
        middle = paint._landmark_pairs({"c": [0.0, 2.0, 3.0]}, {"c": [0.0, 5.0, 6.0]})
        self.assertEqual(middle[0].tolist(), [[0.0, 2.0, 3.0]])

    def test_only_the_landmarks_both_rigs_have_with_as_many_points_count(self):
        canvas = {"a": [1.0, 0.0, 0.0], "b": [0.0, 1.0, 0.0], "c": [0.0, 0.0, 1.0, 0.0, 0.0, 2.0]}
        body = {"a": [2.0, 0.0, 0.0], "c": [0.0, 0.0, 1.0], "d": [9.0, 9.0, 9.0]}
        source, _target = paint._landmark_pairs(canvas, body)
        self.assertEqual(sorted(source[:, 0].tolist()), [-1.0, 1.0])
        self.assertEqual(len(source), 2)

    def test_two_points_closer_on_the_canvas_than_the_merge_distance_are_one(self):
        far = paint.LANDMARK_MERGE * 3
        canvas = {"a": [0.0, 0.0, 0.0], "b": [0.0, 0.0, paint.LANDMARK_MERGE / 2], "c": [0.0, 0.0, far]}
        body = {"a": [1.0, 0.0, 0.0], "b": [2.0, 0.0, 0.0], "c": [3.0, 0.0, 0.0]}
        source, target = paint._landmark_pairs(canvas, body)
        self.assertEqual(len(source), 2)
        self.assertEqual(target[:, 0].tolist(), [1.0, 3.0])


class DyeTests(unittest.TestCase):
    def test_the_color_curves_agree_with_the_standard_and_return_what_they_were_given(self):
        np.testing.assert_allclose(texture.srgb_to_linear(np.array([0.0, 0.04045, 0.5, 1.0])),
                                   [0.0, 0.04045 / 12.92, 0.2140411, 1.0], atol=1e-6)
        values = np.linspace(0.0, 1.0, 101)
        np.testing.assert_allclose(texture.srgb_to_linear(texture.linear_to_srgb(values)), values, atol=1e-9)

    def test_a_linear_value_outside_zero_to_one_is_clamped_on_its_way_to_srgb(self):
        np.testing.assert_allclose(texture.linear_to_srgb(np.array([-1.0, 2.0])), [0.0, 1.0])

    def test_a_hex_color_reads_as_linear_and_writes_back_as_the_same_hex(self):
        np.testing.assert_allclose(texture.color_linear("#FFFFFF"), [1.0, 1.0, 1.0])
        np.testing.assert_allclose(texture.color_linear("#000000"), [0.0, 0.0, 0.0])
        np.testing.assert_allclose(texture.color_linear("#808080"), [0.2158605] * 3, atol=1e-6)
        for color in ("#8D806E", "#000000", "#FFFFFF", "#0A1B2C", "#FF0001", "#7F7F7F"):
            self.assertEqual(texture._hex(texture.color_linear(color)), color)
        for level in range(256):
            color = f"#{level:02X}{level:02X}{level:02X}"
            self.assertEqual(texture._hex(texture.color_linear(color)), color)

    def test_a_texel_the_dye_does_not_reach_is_not_changed(self):
        mask = np.zeros((2, 3, 3))
        np.testing.assert_allclose(texture.dye_factor(mask, ["#102030"]), np.ones((2, 3, 3)))

    def test_a_texel_the_dye_reaches_in_full_takes_the_dye_color_and_half_way_the_middle(self):
        mask = np.zeros((1, 2, 3))
        mask[0, 0, 0] = 1.0
        mask[0, 1, 0] = 0.5
        factor = texture.dye_factor(mask, ["#808080"])
        np.testing.assert_allclose(factor[0, 0], [0.2158605] * 3, atol=1e-6)
        np.testing.assert_allclose(factor[0, 1], [(1 + 0.2158605) / 2] * 3, atol=1e-6)

    def test_every_channel_dyes_its_own_texels_and_channels_multiply_where_they_overlap(self):
        mask = np.zeros((1, 3, 3))
        mask[0, 0, 0] = 1.0
        mask[0, 1, 1] = 1.0
        mask[0, 2, 0] = 1.0
        mask[0, 2, 1] = 1.0
        factor = texture.dye_factor(mask, ["#FFFFFF", "#808080"])
        np.testing.assert_allclose(factor[0, 0], [1.0] * 3)
        np.testing.assert_allclose(factor[0, 1], [0.2158605] * 3, atol=1e-6)
        np.testing.assert_allclose(factor[0, 2], [0.2158605] * 3, atol=1e-6)

    def test_a_dye_with_fewer_colors_than_channels_leaves_the_other_channels_alone(self):
        mask = np.ones((1, 1, 3))
        np.testing.assert_allclose(texture.dye_factor(mask, ["#FFFFFF"]), [[[1.0, 1.0, 1.0]]])


class CoverageReportTests(unittest.TestCase):
    @staticmethod
    def _coverage(**fields):
        values = {"label": "Male", "band": "tight", "covered": np.array([True, True, False, True]),
                  "underlay": np.array([0, 1]), "pull_vertices": np.array([], int), "pull_distances": np.array([]),
                  "body_faces": 4, "body_vertices": 9, "garment_faces": 2, **fields}
        return coverage.Coverage(**values)

    def test_a_garment_that_pulls_in_nothing_says_how_many_faces_it_covers(self):
        self.assertEqual(coverage.summary("Shirt", self._coverage()),
                         "Shirt_Male: covers 3 of 4 body faces, leaves 0 at its edges visible, and pulls in 0 visible vertices")

    def test_the_edge_faces_and_the_largest_pull_in_are_counted_in_millimeters(self):
        report = coverage.summary("Shirt", self._coverage(edge=np.array([False, True, True, False]),
                                                          pull_vertices=np.array([3, 7]),
                                                          pull_distances=np.array([0.001, 0.0024])))
        self.assertEqual(report, "Shirt_Male: covers 3 of 4 body faces, leaves 2 at its edges visible, and pulls in 2 "
                                 "visible vertices by at most 2.4 mm")


class TextureMoveTests(unittest.TestCase):
    """A texture follows a template that was built again: its preview's feature curves are drawn onto
    the template's, and its texels take the paint of the old image where they lie."""

    # A strip of four quads in a row, ten vertices: 0 to 4 along the bottom, 5 to 9 along the top.
    STRIP = [(0, 1, 6, 5), (1, 2, 7, 6), (2, 3, 8, 7), (3, 4, 9, 8)]
    STRIP_CO = np.array([(float(x), 0.0, 0.0) for x in range(5)] + [(float(x), 1.0, 0.0) for x in range(5)])
    TUBE = [(0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]

    def test_the_open_edges_of_a_sheet_are_one_closed_loop_in_order(self):
        loops = texture.open_loops(self.STRIP)
        self.assertEqual(len(loops), 1)
        loop, closed = loops[0]
        self.assertTrue(closed)
        self.assertEqual(sorted(loop), list(range(10)))
        edges = {tuple(sorted((a, b))) for face in self.STRIP for a, b in zip(face, face[1:] + face[:1])}
        for a, b in zip(loop, loop[1:] + loop[:1]):
            self.assertIn(tuple(sorted((a, b))), edges)

    def test_a_tube_has_an_open_loop_at_each_end_and_a_closed_surface_none(self):
        loops = texture.open_loops(self.TUBE)
        self.assertEqual(sorted(sorted(loop) for loop, closed in loops if closed), [[0, 1, 2, 3], [4, 5, 6, 7]])
        self.assertEqual(texture.open_loops(self.TUBE + [(0, 3, 2, 1), (4, 5, 6, 7)]), [])

    def test_the_line_a_loose_part_hangs_from_is_read_in_its_order_along_the_edge(self):
        loose = np.array([0, 0, 0, 0, 0, -1, -1, -1, -1, -1])
        top = np.array([0, 0, 0, 0, 0, 4, 3, 2, 1, 0])
        self.assertEqual(texture.hang_lines(self.STRIP, loose, top), [([9, 8, 7, 6, 5], False)])
        self.assertEqual(texture.hang_lines(self.STRIP, None, None), [])
        self.assertEqual(texture.hang_lines(self.STRIP, np.zeros(10, int), top), [])

    def test_a_hang_line_all_around_closes(self):
        loose = np.array([-1, -1, -1, -1, 1, 1, 1, 1])
        self.assertEqual(texture.hang_lines(self.TUBE, loose, np.array([0, 1, 2, 3, 0, 1, 2, 3])), [([0, 1, 2, 3], True)])

    def test_the_nearest_point_of_a_line_lies_on_its_segments(self):
        line = np.array([(0.0, 0.0, 0.0), (2.0, 0.0, 0.0), (2.0, 2.0, 0.0)])
        points = np.array([(1.0, 1.0, 0.0), (3.0, 1.0, 0.0), (-1.0, 0.5, 0.0)])
        onto, far = texture.nearest_on(points, line, False)
        np.testing.assert_allclose(onto, [(1.0, 0.0, 0.0), (2.0, 1.0, 0.0), (0.0, 0.0, 0.0)])
        np.testing.assert_allclose(far, [1.0, 1.0, np.sqrt(1.25)])
        # A closed line also has the segment from its last point back to its first.
        onto, _far = texture.nearest_on(np.array([(1.1, 1.0, 0.0)]), line, True)
        np.testing.assert_allclose(onto, [(1.05, 1.05, 0.0)])

    def test_a_move_spreads_evenly_between_the_fixed_vertices(self):
        fixed = np.zeros(10, bool)
        fixed[[0, 5, 4, 9]] = True
        moves = np.zeros((10, 3))
        moves[[4, 9]] = (0.0, 0.0, 4.0)
        out = texture.spread(10, self.STRIP, fixed, moves)
        np.testing.assert_allclose(out[:5, 2], [0.0, 1.0, 2.0, 3.0, 4.0], atol=1e-6)
        np.testing.assert_allclose(out[5:, 2], [0.0, 1.0, 2.0, 3.0, 4.0], atol=1e-6)
        np.testing.assert_allclose(out[:, :2], 0.0, atol=1e-9)

    def test_a_piece_that_no_fixed_vertex_is_joined_to_stays(self):
        faces = self.STRIP + [(10, 11, 12)]
        fixed = np.zeros(13, bool)
        fixed[[0, 5]] = True
        moves = np.zeros((13, 3))
        moves[[0, 5]] = (1.0, 0.0, 0.0)
        out = texture.spread(13, faces, fixed, moves)
        np.testing.assert_allclose(out[:10], np.tile((1.0, 0.0, 0.0), (10, 1)), atol=1e-5)
        np.testing.assert_allclose(out[10:], 0.0, atol=1e-6)

    def test_an_edge_is_drawn_onto_the_templates_and_the_cloth_between_follows(self):
        # The template's sheet reaches half a unit farther at its right end; the left end stays.
        curves = [("edge", [0, 5], False), ("edge", [4, 9], False)]
        theirs = [("edge", np.array([(0.0, 0.0, 0.0), (0.0, 1.0, 0.0)]), False),
                  ("edge", np.array([(4.5, 0.0, 0.0), (4.5, 1.0, 0.0)]), False)]
        out, notes = texture.drawn(self.STRIP_CO, self.STRIP, curves, theirs, reach=1.0)
        np.testing.assert_allclose(out[:5, 0], [0.0, 1.125, 2.25, 3.375, 4.5], atol=1e-6)
        np.testing.assert_allclose(out[5:, 0], [0.0, 1.125, 2.25, 3.375, 4.5], atol=1e-6)
        np.testing.assert_allclose(out[:, 1:], self.STRIP_CO[:, 1:], atol=1e-9)
        self.assertEqual(len(notes), 2)
        self.assertIn("500.0 mm", notes[1])

    def test_a_curve_out_of_reach_stays_and_holds_its_cloth(self):
        far = [("edge", np.array([(0.0, 0.0, 9.0), (0.0, 1.0, 9.0)]), False)]
        out, notes = texture.drawn(self.STRIP_CO, self.STRIP, [("edge", [0, 5], False)], far, reach=0.1)
        np.testing.assert_allclose(out, self.STRIP_CO, atol=1e-9)
        self.assertIn("stays", notes[0])
        # A curve only takes one of its own kind.
        out, notes = texture.drawn(self.STRIP_CO, self.STRIP, [("hang", [0, 5], False)],
                                   [("edge", np.array([(0.0, 0.0, 0.01), (0.0, 1.0, 0.01)]), False)])
        np.testing.assert_allclose(out, self.STRIP_CO, atol=1e-9)
        self.assertIn("stays", notes[0])

    def test_an_open_edge_goes_before_a_hang_line_at_a_shared_vertex(self):
        theirs = [("edge", np.array([(0.0, 0.0, 0.02), (0.0, 1.0, 0.02)]), False),
                  ("hang", np.array([(0.0, 1.0, 0.05), (4.0, 1.0, 0.05)]), False)]
        curves = [("edge", [0, 5], False), ("hang", [5, 6, 7, 8, 9], False)]
        out, _notes = texture.drawn(self.STRIP_CO, self.STRIP, curves, theirs, reach=1.0)
        self.assertAlmostEqual(out[5, 2], 0.02)
        self.assertAlmostEqual(out[9, 2], 0.05)

    def test_the_reaches_are_centimeters(self):
        self.assertTrue(0.02 <= texture.CURVE_REACH <= 0.2)
        self.assertTrue(0.005 <= texture.SIDE_REACH <= 0.05)

    def test_a_texel_is_read_between_its_four_nearest_and_every_channel_on_its_own(self):
        image = np.zeros((2, 2, 4))
        image[0, 0] = (1.0, 0.0, 0.0, 1.0)
        image[0, 1] = (0.0, 1.0, 0.0, 0.0)
        image[1, 0] = (0.0, 0.0, 1.0, 1.0)
        image[1, 1] = (1.0, 1.0, 1.0, 1.0)
        centres = np.array([(0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75)])
        np.testing.assert_allclose(texture.sampled(image, centres), image.reshape(4, 4))
        # Halfway between a painted texel and a hole the color is the mean of the two colors: the
        # hole's color counts, and the hole does not darken it.
        np.testing.assert_allclose(texture.sampled(image, np.array([(0.5, 0.25)])), [(0.5, 0.5, 0.0, 0.5)])
        # Beyond the image its edge texels hold.
        np.testing.assert_allclose(texture.sampled(image, np.array([(-1.0, -1.0)])), [image[0, 0]])

    def test_paint_grows_over_the_margin_and_the_rest_is_black_and_opaque(self):
        image = np.zeros((9, 9, 4), np.float32)
        filled = np.zeros((9, 9), bool)
        image[4, 4] = (0.2, 0.4, 0.6, 0.5)
        filled[4, 4] = True
        out = texture.grown(image, filled, margin=2)
        np.testing.assert_allclose(out[2:7, 2:7], np.tile((0.2, 0.4, 0.6, 0.5), (5, 5, 1)), atol=1e-6)
        np.testing.assert_allclose(out[0, 0], (0.0, 0.0, 0.0, 1.0))
        np.testing.assert_allclose(out[4, 7], (0.0, 0.0, 0.0, 1.0))
        # Between two paints a texel takes the mean of its filled neighbours.
        image[4, 6] = (1.0, 1.0, 1.0, 1.0)
        filled[4, 6] = True
        np.testing.assert_allclose(texture.grown(image, filled, margin=1)[4, 5], (0.6, 0.7, 0.8, 0.75), atol=1e-6)
        self.assertGreaterEqual(texture.BAKE_MARGIN, 4)


if __name__ == "__main__":
    unittest.main()
