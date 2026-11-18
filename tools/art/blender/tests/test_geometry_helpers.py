"""The small geometric helpers of the cloth chains, the hanging of loose parts, the preview colors, and
the rounding keys that the Blender modules use, on plain numbers with answers that follow from the
input. Runs without Blender: see blender_source.py."""

import math
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import load  # noqa: E402

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))

from art_pipeline import cloth_core  # noqa: E402

cloth = load("art_pipeline/cloth.py", "_filled", "_without_grooves", "_angle", "_ring_order", "_neighbours",
             GROOVE_PASSES=2, ANGLES=cloth_core.ANGLES)
hang = load("art_pipeline/hang.py", "parts", "_smooth", "_gap", "_spread", "_half", "EDGE_GAP", "LEAVE")
skinned = load("art_pipeline/skinned.py", "_key", "_rounded", "CHANNEL_PATTERN")
preview = load("art_pipeline/preview.py", "_srgb_to_linear", "_gap_color", "CAGE_GAP_SCALE", "CAGE_GAP_SRGB")


class FilledRadiiTests(unittest.TestCase):
    def test_a_direction_without_a_vertex_is_taken_between_the_nearest_directions_that_have_one(self):
        self.assertEqual(cloth._filled([1.0, None, 3.0, None], 9.0), [1.0, 2.0, 3.0, 2.0])
        self.assertEqual(cloth._filled([2.0, None, None, 8.0], 9.0), [2.0, 4.0, 6.0, 8.0])

    def test_the_gap_wraps_around_the_bone(self):
        # From 8.0 at 2 round through 3 and 0 to 4.0 at 1: three steps of a third of the difference.
        for got, want in zip(cloth._filled([None, 4.0, 8.0, None], 9.0), [16 / 3, 4.0, 8.0, 20 / 3]):
            self.assertAlmostEqual(got, want)

    def test_one_known_direction_fills_the_ring_with_its_radius(self):
        self.assertEqual(cloth._filled([None, 5.0, None], 9.0), [5.0, 5.0, 5.0])

    def test_a_ring_without_any_vertex_takes_the_fallback_radius(self):
        self.assertEqual(cloth._filled([None, None, None], 0.05), [0.05, 0.05, 0.05])

    def test_known_radii_never_change(self):
        radii = [0.3, None, 0.1, 0.2, None, None, 0.4, 0.5]
        filled = cloth._filled(radii, 1.0)
        for known, result in zip(radii, filled):
            if known is not None:
                self.assertEqual(result, known)
        self.assertTrue(all(0.1 <= r <= 0.5 for r in filled))


class GroovesTests(unittest.TestCase):
    def test_a_narrow_groove_is_filled_to_the_middle_of_its_neighbors(self):
        self.assertEqual(cloth._without_grooves([1.0, 1.0, 0.0, 1.0, 1.0]), [1.0, 1.0, 1.0, 1.0, 1.0])

    def test_the_fill_takes_two_passes_and_the_ring_wraps(self):
        self.assertEqual(cloth._without_grooves([0.0, 0.0, 3.0, 0.0, 0.0, 0.0]), [0.75, 1.5, 3.0, 1.5, 0.75, 0.0])

    def test_no_radius_ever_shrinks_and_a_round_ring_stays_round(self):
        radii = [0.2, 0.5, 0.1, 0.4, 0.3, 0.3]
        for before, after in zip(radii, cloth._without_grooves(radii)):
            self.assertGreaterEqual(after, before)
        self.assertEqual(cloth._without_grooves([0.3] * 8), [0.3] * 8)


class ChainOrderTests(unittest.TestCase):
    def test_a_chain_is_placed_by_the_angle_of_its_first_bone_negative_on_the_left(self):
        self.assertEqual(cloth._angle("SkirtFront1"), 0.0)
        self.assertEqual(cloth._angle("SkirtFrontSide1_R"), 45.0)
        self.assertEqual(cloth._angle("SkirtFrontSide1_L"), -45.0)
        self.assertEqual(cloth._angle("SkirtBack1"), 180.0)
        self.assertEqual(cloth._angle("CloakInner1_L"), -157.5)
        self.assertEqual(cloth._angle("Hood1"), 180.0)

    def test_the_skirt_ring_runs_from_the_front_toward_the_right_and_round(self):
        order = [cloth._ring_order("skirt", f"{name}1{side}") for name, side in (
            ("SkirtFront", ""), ("SkirtFrontSide", "_R"), ("SkirtSide", "_R"), ("SkirtBackSide", "_R"), ("SkirtBack", ""),
            ("SkirtBackSide", "_L"), ("SkirtSide", "_L"), ("SkirtFrontSide", "_L"))]
        self.assertEqual(order, [0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0])

    def test_the_cloak_sheet_runs_from_the_left_across_the_back_to_the_right(self):
        order = [cloth._ring_order("cloak", f"{name}1{side}") for name, side in (
            ("CloakOuter", "_L"), ("CloakInner", "_L"), ("CloakCenter", ""), ("CloakInner", "_R"), ("CloakOuter", "_R"))]
        self.assertEqual(order, [-45.0, -22.5, 0.0, 22.5, 45.0])

    def test_the_skirt_ring_closes_and_the_cloak_sheet_stays_open(self):
        skirts = ["skirt"] * 5
        self.assertEqual(cloth._neighbours(skirts, 0), (4, 1))
        self.assertEqual(cloth._neighbours(skirts, 2), (1, 3))
        self.assertEqual(cloth._neighbours(skirts, 4), (3, 0))
        cloaks = ["cloak"] * 3
        self.assertEqual(cloth._neighbours(cloaks, 0), (-1, 1))
        self.assertEqual(cloth._neighbours(cloaks, 1), (0, 2))
        self.assertEqual(cloth._neighbours(cloaks, 2), (1, -1))

    def test_a_hood_or_a_lone_chain_has_no_neighbors(self):
        self.assertEqual(cloth._neighbours(["hood"], 0), (-1, -1))
        self.assertEqual(cloth._neighbours(["hood", "hood"], 1), (-1, -1))
        self.assertEqual(cloth._neighbours(["skirt"], 0), (-1, -1))
        self.assertEqual(cloth._neighbours(["cloak"], 0), (-1, -1))

    def test_neighbors_are_found_among_the_chains_of_the_same_kind_laid_in_order(self):
        kinds = ["skirt", "skirt", "skirt", "cloak", "cloak", "hood"]
        self.assertEqual(cloth._neighbours(kinds, 0), (2, 1))
        self.assertEqual(cloth._neighbours(kinds, 3), (-1, 4))
        self.assertEqual(cloth._neighbours(kinds, 4), (3, -1))
        self.assertEqual(cloth._neighbours(kinds, 5), (-1, -1))


class HangingTests(unittest.TestCase):
    def test_the_loose_parts_of_a_recipe(self):
        part = {"line": "SkirtWaist", "flare": 0.08}
        self.assertEqual(hang.parts({"hang": [part]}), [part])
        self.assertEqual(hang.parts({"regions": []}), [])
        self.assertEqual(hang.parts("modeled"), [])

    def test_the_parts_are_a_list_of_their_own(self):
        recipe = {"hang": [{"flare": 0.0}]}
        hang.parts(recipe).append({"flare": 1.0})
        self.assertEqual(len(recipe["hang"]), 1)

    def test_the_smooth_step_is_zero_at_the_start_one_at_the_end_and_clamped(self):
        self.assertEqual(hang._smooth(-1.0), 0.0)
        self.assertEqual(hang._smooth(0.0), 0.0)
        self.assertEqual(hang._smooth(0.5), 0.5)
        self.assertEqual(hang._smooth(1.0), 1.0)
        self.assertEqual(hang._smooth(9.0), 1.0)
        self.assertAlmostEqual(hang._smooth(0.25), 0.15625)

    def test_the_cloth_keeps_the_edge_s_gap_right_below_it_and_leaves_to_the_margin(self):
        self.assertAlmostEqual(hang._gap(0.0, 0.03), hang.EDGE_GAP)
        self.assertAlmostEqual(hang._gap(0.02, 0.03), hang.EDGE_GAP + 0.02 * hang.LEAVE)
        self.assertEqual(hang._gap(5.0, 0.03), 0.03)
        # A margin smaller than the edge's gap wins at once.
        self.assertEqual(hang._gap(0.0, 0.001), 0.001)

    def test_a_part_across_the_back_fans_out_over_the_depth_of_its_first_joint(self):
        self.assertEqual(hang._spread(0.0, 0.2), 0.0)
        self.assertEqual(hang._spread(0.1, 0.2), 0.5)
        self.assertEqual(hang._spread(0.2, 0.2), 1.0)
        self.assertEqual(hang._spread(0.5, 0.2), 1.0)
        # Without a joint to fan over it is at its width from the edge.
        self.assertEqual(hang._spread(0.0, 0.0), 1.0)

    def test_the_half_width_goes_from_the_top_to_the_width_and_flares_toward_the_hem(self):
        part = {"flare": 0.08}
        self.assertAlmostEqual(hang._half(part, 0.1, 0.3, 0.2, 1.0, 0.0), 0.1)
        self.assertAlmostEqual(hang._half(part, 0.1, 0.3, 0.2, 1.0, 0.1), 0.1 + 0.2 * 0.5 + 0.08 * 0.1)
        self.assertAlmostEqual(hang._half(part, 0.1, 0.3, 0.2, 1.0, 1.0), 0.3 + 0.08)
        self.assertAlmostEqual(hang._half({"flare": 0}, 0.1, 0.3, 0.2, 1.0, 1.0), 0.3)


class RoundingKeyTests(unittest.TestCase):
    def test_a_position_is_keyed_by_its_hundred_thousandths(self):
        self.assertEqual(skinned._key((0.123456, -1.5, 2.000004)), (12346, -150000, 200000))
        self.assertEqual(skinned._key((0.0, 0.0, 0.0)), (0, 0, 0))

    def test_every_copy_of_the_key_in_the_pipeline_rounds_alike(self):
        copies = [load(path, "_key")._key for path in ("art_pipeline/bodies.py", "art_pipeline/skinned.py",
                                                       "human_base/checks.py", "human_base/rig.py")]
        for point in ((0.123456, -1.5, 2.000004), (0.000005, -0.000005, 0.5), (1e-9, 1234.56789, -3.3), (0, 0, 0)):
            self.assertEqual({copy(point) for copy in copies}, {skinned._key(point)}, point)

    def test_a_distance_is_written_to_a_hundredth_of_a_millimeter_and_never_as_negative_zero(self):
        self.assertEqual(skinned._rounded(1.234567), "1.23457")
        self.assertEqual(skinned._rounded(0.0), "0.00000")
        self.assertEqual(skinned._rounded(-0.0), "0.00000")
        self.assertEqual(skinned._rounded(-0.000001), "0.00000")
        self.assertEqual(skinned._rounded(-2.5), "-2.50000")

    def test_an_animation_channel_names_its_bone_and_its_property(self):
        match = skinned.CHANNEL_PATTERN.match('pose.bones["UpperArm_L"].rotation_quaternion')
        self.assertEqual(match.groups(), ("UpperArm_L", "rotation_quaternion"))
        self.assertEqual(skinned.CHANNEL_PATTERN.match('pose.bones["Hips"].location').groups(), ("Hips", "location"))
        for other in ('pose.bones["Hips"].scale', 'pose.bones["Hips"].rotation_euler', 'location', 'pose.bones[].location'):
            self.assertIsNone(skinned.CHANNEL_PATTERN.match(other), other)


class PreviewColorTests(unittest.TestCase):
    def test_srgb_to_linear_keeps_the_ends_and_follows_the_standard(self):
        self.assertEqual(preview._srgb_to_linear(0.0), 0.0)
        self.assertAlmostEqual(preview._srgb_to_linear(1.0), 1.0)
        self.assertAlmostEqual(preview._srgb_to_linear(0.04), 0.04 / 12.92)
        self.assertAlmostEqual(preview._srgb_to_linear(0.5), 0.2140411, places=6)

    def test_a_gap_is_blue_at_the_skin_yellow_halfway_and_red_at_the_far_end(self):
        low, middle, high = preview.CAGE_GAP_SRGB
        scale = preview.CAGE_GAP_SCALE

        def linear(color):
            return tuple(preview._srgb_to_linear(c) for c in color) + (1.0,)

        for distance, expected in ((0.0, linear(low)), (-1.0, linear(low)), (scale / 2, linear(middle)),
                                   (scale, linear(high)), (1.0, linear(high))):
            with self.subTest(distance=distance):
                for got, want in zip(preview._gap_color(distance), expected):
                    self.assertAlmostEqual(got, want)

    def test_a_gap_between_the_stops_blends_their_srgb_colors(self):
        low, middle, _high = preview.CAGE_GAP_SRGB
        quarter = preview._gap_color(preview.CAGE_GAP_SCALE / 4)
        for got, a, b in zip(quarter, low, middle):
            self.assertAlmostEqual(got, preview._srgb_to_linear((a + b) / 2))
        self.assertEqual(quarter[3], 1.0)

    def test_the_stops_are_srgb_colors(self):
        for color in preview.CAGE_GAP_SRGB:
            self.assertTrue(all(0.0 <= c <= 1.0 for c in color))
        self.assertTrue(math.isfinite(preview.CAGE_GAP_SCALE) and preview.CAGE_GAP_SCALE > 0)


if __name__ == "__main__":
    unittest.main()
