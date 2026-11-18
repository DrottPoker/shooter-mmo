"""The comparison of a model with its reference pictures, as far as it needs numpy but not Blender: the
figure of a picture, with transparency or on a painted background, the overlap of two figures, the
overlay image, where a picture's middle lies, and the size it is compared at. Skipped without numpy,
like test_numeric_logic.py. See blender_source.py."""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from blender_source import load  # noqa: E402

try:
    import numpy as np
except ImportError:
    raise unittest.SkipTest("numpy is not installed; see requirements.txt") from None

reference = load("art_pipeline/reference.py", "figure_mask", "resample", "overlap", "overlay", "picture_middle",
                 "render_size", "BACKGROUND_DISTANCE", "MAX_SIZE", "BOTH_COLOR", "PICTURE_COLOR", "MODEL_COLOR",
                 "NEITHER_GREY", numpy=True)


def picture(height: int, width: int, color=(0.8, 0.8, 0.8), alpha: float = 1.0) -> np.ndarray:
    pixels = np.empty((height, width, 4), dtype=np.float32)
    pixels[..., :3] = color
    pixels[..., 3] = alpha
    return pixels


class FigureTests(unittest.TestCase):
    def test_a_picture_with_transparency_has_its_figure_where_it_is_opaque_whatever_its_colors(self):
        pixels = picture(100, 60, alpha=0.0)
        pixels[40:60, 20:30, 3] = 1.0
        pixels[0:10, :, :3] = 0.1  # dark, but transparent
        mask = reference.figure_mask(pixels)
        self.assertEqual(int(mask.sum()), 200)
        self.assertTrue(mask[40:60, 20:30].all())

    def test_an_opaque_picture_has_its_figure_where_the_color_differs_from_the_background(self):
        pixels = picture(100, 80)
        pixels[30:70, 30:50, :3] = (0.2, 0.3, 0.4)
        mask = reference.figure_mask(pixels)
        self.assertEqual(int(mask.sum()), 40 * 20)
        self.assertTrue(mask[30:70, 30:50].all())

    def test_a_color_within_the_background_distance_is_background(self):
        pixels = picture(100, 80)
        step = reference.BACKGROUND_DISTANCE * 0.9
        pixels[30:70, 30:50, 0] += step
        self.assertFalse(reference.figure_mask(pixels).any())
        pixels[30:70, 30:50, 0] += reference.BACKGROUND_DISTANCE
        self.assertTrue(reference.figure_mask(pixels)[30:70, 30:50].all())

    def test_a_soft_gradient_behind_the_figure_is_background_row_by_row(self):
        # From light at the top to dark at the bottom, far more than the distance over the picture.
        pixels = picture(100, 80)
        pixels[..., :3] = np.linspace(0.2, 0.8, 100, dtype=np.float32)[:, None, None]
        pixels[40:60, 30:50, :3] = (0.9, 0.1, 0.1)
        mask = reference.figure_mask(pixels)
        self.assertEqual(int(mask.sum()), 20 * 20)

    def test_the_bottom_two_percent_of_an_opaque_picture_is_a_ground_line_and_left_out(self):
        pixels = picture(200, 80)
        pixels[0:10, 20:60, :3] = 0.1
        mask = reference.figure_mask(pixels)
        self.assertFalse(mask[0:4].any())
        self.assertTrue(mask[4:10, 20:60].all())

    def test_resampling_takes_the_pixel_under_each_new_middle(self):
        mask = np.array([[True, False], [False, True]])
        self.assertEqual(reference.resample(mask, 4, 4).tolist(),
                         [[True, True, False, False], [True, True, False, False],
                          [False, False, True, True], [False, False, True, True]])
        self.assertEqual(reference.resample(mask, 2, 2).tolist(), mask.tolist())
        # Halved, the middles of the new pixels lie on the old pixels 1 and 3.
        corner = np.zeros((4, 4), dtype=bool)
        corner[0, 0] = corner[3, 1] = True
        self.assertEqual(reference.resample(corner, 2, 2).tolist(), [[False, False], [True, False]])


class OverlapTests(unittest.TestCase):
    def setUp(self):
        self.picture = np.zeros((10, 10), dtype=bool)
        self.picture[0:4, 0:5] = True   # 20 pixels
        self.model = np.zeros((10, 10), dtype=bool)
        self.model[0:4, 2:7] = True     # 20 pixels, 12 of them shared

    def test_the_overlap_is_the_intersection_over_the_union_and_the_shares_are_of_the_picture(self):
        both, picture_only, model_only = reference.overlap(self.picture, self.model)
        self.assertAlmostEqual(both, 12 / 28)
        self.assertAlmostEqual(picture_only, 8 / 20)
        self.assertAlmostEqual(model_only, 8 / 20)

    def test_equal_figures_overlap_fully_and_no_figures_not_at_all(self):
        self.assertEqual(reference.overlap(self.picture, self.picture), (1.0, 0.0, 0.0))
        empty = np.zeros((10, 10), dtype=bool)
        self.assertEqual(reference.overlap(empty, empty), (0.0, 0.0, 0.0))
        self.assertEqual(reference.overlap(empty, self.model), (0.0, 0.0, 0.0))

    def test_the_overlay_colors_both_the_picture_only_the_model_only_and_neither(self):
        out = reference.overlay(self.picture, self.model)
        self.assertEqual(out.shape, (10, 10, 4))
        self.assertTrue((out[..., 3] == 1.0).all())
        np.testing.assert_allclose(out[0, 3, :3], reference.BOTH_COLOR)
        np.testing.assert_allclose(out[0, 0, :3], reference.PICTURE_COLOR)
        np.testing.assert_allclose(out[0, 6, :3], reference.MODEL_COLOR)
        np.testing.assert_allclose(out[9, 9, :3], (reference.NEITHER_GREY,) * 3)


class PictureTests(unittest.TestCase):
    def test_a_picture_from_its_origin_has_its_middle_at_half_its_size_its_larger_side_the_display_size(self):
        self.assertEqual(reference.picture_middle(1240, 790, 2.0, (0.0, 0.0)), (1.0, 790 / 1240))
        self.assertEqual(reference.picture_middle(400, 800, 2.0, (0.0, 0.0)), (0.5, 1.0))

    def test_a_centered_picture_has_its_middle_at_the_origin(self):
        self.assertEqual(reference.picture_middle(300, 200, 1.5, (-0.5, -0.5)), (0.0, 0.0))
        self.assertEqual(reference.picture_middle(300, 200, 1.5, (-1.0, 0.0)), (-0.75, 0.5))

    def test_a_picture_is_compared_at_its_size_up_to_the_largest(self):
        self.assertEqual(reference.render_size(1240, 790), (1240, 790))
        self.assertEqual(reference.render_size(reference.MAX_SIZE * 2, reference.MAX_SIZE), (reference.MAX_SIZE, reference.MAX_SIZE // 2))
        self.assertEqual(reference.render_size(1000, 4096), (500, reference.MAX_SIZE))


if __name__ == "__main__":
    unittest.main()
