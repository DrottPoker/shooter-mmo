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

cage = load("art_pipeline/cage.py", "Net", "Part", "hull", "centroid", "_shares", "_along", "_turning", "REGIONS", numpy=True)
garment = load("art_pipeline/garment.py", "_limited", "_checksum", "_rounded_weights", "MAX_INFLUENCES", "version_name",
               numpy=True)
bodies = load("art_pipeline/bodies.py", "place_keys", "mirror_key", "ZONES")
rig = load("human_base/rig.py", "_mirror_columns", "_mirrored", "symmetrize", "limit", "_move_excess", "_segment_distance",
           "_smooth", "_around", "MIN_WEIGHT", "MAX_INFLUENCES", bodies=bodies, numpy=True)
paint = load("art_pipeline/paint.py", "crop", "_turn", "_landmark_pairs", "CROP_BLOCK", "MIRROR_MARGIN", "LANDMARK_MERGE",
             numpy=True)
texture = load("art_pipeline/texture.py", "srgb_to_linear", "linear_to_srgb", "color_linear", "_hex", "dye_factor", numpy=True)
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


if __name__ == "__main__":
    unittest.main()
